using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Setup;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed partial class AnnouncementHttpTests
{
    private const string OwnerPassword = "mild river orbit velvet canyon";
    private const string UserPassword = "silver meadow lantern compass";

    [Fact]
    public async Task UnifiedComposerHasOneMessageSurfaceAndMediaPreviewIsPrivate()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient ownerClient = CreateClient(factory);
        using HttpClient viewerClient = CreateClient(factory);
        using HttpClient anonymousClient = CreateClient(factory);
        Guid ownerId = await InitializeOwnerAsync(factory.Services);
        _ = await CreateAndActivateAsync(
            factory.Services,
            ownerId,
            "media-viewer",
            SystemRoles.Viewer);
        await LoginAsync(ownerClient, "owner-local", OwnerPassword);
        await LoginAsync(viewerClient, "media-viewer", UserPassword);

        string newHtml = await ownerClient.GetStringAsync("/Announcements/New");
        Assert.True(Regex.Count(newHtml, "name=\"MessageContent\"", RegexOptions.CultureInvariant) == 1);
        Assert.Contains("data-announcement-composer", newHtml, StringComparison.Ordinal);
        Assert.Contains("data-markdown-command=\"bold\"", newHtml, StringComparison.Ordinal);
        Assert.Contains("type=\"button\"", newHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"PlainContent\"", newHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"EmbedDescription\"", newHtml, StringComparison.Ordinal);

        byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x61, 0x62];
        Guid announcementId = Guid.NewGuid();
        Guid mediaId;
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            IAnnouncementService announcements = scope.ServiceProvider.GetRequiredService<IAnnouncementService>();
            Assert.Equal(
                AnnouncementOperationStatus.Succeeded,
                (await announcements.CreateAsync(
                    announcementId,
                    "Internal title",
                    "Message",
                    ownerId,
                    [new AnnouncementMediaUpload(
                        png.ToArray(),
                        "synthetic.png",
                        "image/png",
                        "Safe alt",
                        false,
                        AnnouncementMediaPresentation.Attachment,
                        0)])).Status);
            mediaId = Assert.Single((await announcements.GetAsync(announcementId))!.Media).Id;
        }

        HttpResponseMessage preview = await ownerClient.GetAsync($"/Announcements/{announcementId}/Media/{mediaId}");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("image/png", preview.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await preview.Content.ReadAsByteArrayAsync());
        Assert.Contains("no-store", preview.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("nosniff", preview.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("inline", preview.Content.Headers.ContentDisposition?.DispositionType);

        HttpResponseMessage crossAnnouncement = await ownerClient.GetAsync($"/Announcements/{Guid.NewGuid()}/Media/{mediaId}");
        Assert.Equal(HttpStatusCode.NotFound, crossAnnouncement.StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await viewerClient.GetAsync($"/Announcements/{announcementId}/Media/{mediaId}")).StatusCode);
        HttpResponseMessage anonymous = await anonymousClient.GetAsync($"/Announcements/{announcementId}/Media/{mediaId}");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
    }

    [Fact]
    public async Task MultipartCreateAndRevisionBoundEditPersistThenRemoveMedia()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = CreateClient(factory);
        _ = await InitializeOwnerAsync(factory.Services);
        await LoginAsync(client, "owner-local", OwnerPassword);
        string newHtml = await client.GetStringAsync("/Announcements/New");
        Guid announcementId = Guid.Parse(GetHiddenValue(newHtml, "AnnouncementId"));
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x71, 0x72];
        using var createForm = new MultipartFormDataContent();
        AddField(createForm, "__RequestVerificationToken", GetAntiforgeryToken(newHtml));
        AddField(createForm, "AnnouncementId", announcementId.ToString());
        AddField(createForm, "Title", "Internal media draft");
        AddField(createForm, "MessageContent", "Markdown message");
        AddField(createForm, "NewImageAltTexts[0]", "Initial alt");
        AddField(createForm, "NewImageSpoilers[0]", "true");
        AddField(createForm, "NewImagePresentations[0]", "FeaturedImage");
        AddField(createForm, "NewImageSortOrders[0]", "0");
        var image = new ByteArrayContent(png);
        image.Headers.ContentType = new("image/png");
        createForm.Add(image, "NewImages", "synthetic.png");

        HttpResponseMessage created = await client.PostAsync("/Announcements/New", createForm);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        string editHtml = await client.GetStringAsync($"/Announcements/{announcementId}/Edit");
        Assert.Contains("Initial alt", editHtml, StringComparison.Ordinal);
        Assert.Contains($"/Announcements/{announcementId}/Media/", editHtml, StringComparison.Ordinal);
        string mediaId = GetHiddenValue(editHtml, "ExistingMedia[0].Id");
        string mediaRevision = GetHiddenValue(editHtml, "ExistingMedia[0].Revision");
        string revision = GetHiddenValue(editHtml, "Revision");

        HttpResponseMessage removed = await client.PostAsync(
            $"/Announcements/{announcementId}/Edit",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(editHtml)),
                ("Revision", revision),
                ("Title", "Internal media draft"),
                ("MessageContent", "Markdown message"),
                ("ExistingMedia[0].Id", mediaId),
                ("ExistingMedia[0].Revision", mediaRevision),
                ("ExistingMedia[0].SortOrder", "0"),
                ("ExistingMedia[0].AltText", "Initial alt"),
                ("ExistingMedia[0].IsSpoiler", "true"),
                ("ExistingMedia[0].Presentation", "FeaturedImage"),
                ("ExistingMedia[0].Remove", "true")));
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);

        await using AsyncServiceScope verification = factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext db = verification.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
        Assert.Empty(await db.AnnouncementMediaAssets.Where(value => value.AnnouncementId == announcementId).ToArrayAsync());
        Assert.Equal(2, await db.Announcements.Where(value => value.Id == announcementId).Select(value => value.Revision).SingleAsync());
    }

    [Fact]
    public async Task OwnerCompletesDraftEditArchiveRestoreAndConfirmedDeleteWorkflow()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = CreateClient(factory);
        Guid ownerId = await InitializeOwnerAsync(factory.Services);
        await LoginAsync(client, "owner-local", OwnerPassword);

        HttpResponseMessage newPage = await client.GetAsync("/Announcements/New");
        string newHtml = await newPage.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, newPage.StatusCode);
        AssertNoStoreAndSecurityHeaders(newPage);
        Guid announcementId = Guid.Parse(GetHiddenValue(newHtml, "AnnouncementId"));
        string scriptTitle = "<script>title marker</script>";
        string scriptBody = "First paragraph\n\n<script>body marker</script>";

        HttpResponseMessage created = await client.PostAsync(
            "/Announcements/New",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(newHtml)),
                ("AnnouncementId", announcementId.ToString()),
                ("Title", scriptTitle),
                ("MessageContent", scriptBody)));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal(
            $"/Announcements/{announcementId}?notice=created",
            created.Headers.Location?.OriginalString);
        Assert.DoesNotContain(scriptTitle, created.Headers.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(scriptBody, created.Headers.ToString(), StringComparison.Ordinal);

        HttpResponseMessage details = await client.GetAsync(created.Headers.Location);
        string detailsHtml = await details.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        AssertNoStoreAndSecurityHeaders(details);
        Assert.Contains("&lt;script&gt;title marker&lt;/script&gt;", detailsHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;body marker&lt;/script&gt;", detailsHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(scriptTitle, detailsHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(scriptBody, detailsHtml, StringComparison.Ordinal);
        Assert.Contains("announcement-body", detailsHtml, StringComparison.Ordinal);

        string listHtml = await client.GetStringAsync("/Announcements");
        Assert.Contains("&lt;script&gt;title marker&lt;/script&gt;", listHtml, StringComparison.Ordinal);

        string editHtml = await client.GetStringAsync($"/Announcements/{announcementId}/Edit");
        long firstRevision = ParseRevision(GetHiddenValue(editHtml, "Revision"));
        HttpResponseMessage updated = await client.PostAsync(
            $"/Announcements/{announcementId}/Edit",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(editHtml)),
                ("Revision", FormatRevision(firstRevision)),
                ("Title", "Edited draft"),
                ("MessageContent", "Edited first line\nEdited second line")));
        Assert.Equal(HttpStatusCode.Redirect, updated.StatusCode);

        details = await client.GetAsync(updated.Headers.Location);
        detailsHtml = await details.Content.ReadAsStringAsync();
        long secondRevision = ParseRevision(GetHiddenValue(detailsHtml, "Revision"));
        HttpResponseMessage archived = await client.PostAsync(
            $"/Announcements/{announcementId}/Archive",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(detailsHtml)),
                ("Revision", FormatRevision(secondRevision))));
        Assert.Equal(HttpStatusCode.Redirect, archived.StatusCode);

        HttpResponseMessage archivedEdit = await client.GetAsync(
            $"/Announcements/{announcementId}/Edit");
        Assert.Equal(HttpStatusCode.Redirect, archivedEdit.StatusCode);
        Assert.Contains(
            "notice=readonly",
            archivedEdit.Headers.Location?.OriginalString,
            StringComparison.Ordinal);
        string archivedList = await client.GetStringAsync("/Announcements?status=Archived");
        Assert.Contains("Edited draft", archivedList, StringComparison.Ordinal);

        details = await client.GetAsync(archived.Headers.Location);
        detailsHtml = await details.Content.ReadAsStringAsync();
        long thirdRevision = ParseRevision(GetHiddenValue(detailsHtml, "Revision"));
        HttpResponseMessage restored = await client.PostAsync(
            $"/Announcements/{announcementId}/Restore",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(detailsHtml)),
                ("Revision", FormatRevision(thirdRevision))));
        Assert.Equal(HttpStatusCode.Redirect, restored.StatusCode);

        HttpResponseMessage deletePage = await client.GetAsync(
            $"/Announcements/{announcementId}/Delete");
        string deleteHtml = await deletePage.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, deletePage.StatusCode);
        Assert.Contains("cannot be undone", deleteHtml, StringComparison.OrdinalIgnoreCase);
        long fourthRevision = ParseRevision(GetHiddenValue(deleteHtml, "Revision"));
        HttpResponseMessage deleted = await client.PostAsync(
            $"/Announcements/{announcementId}/Delete",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(deleteHtml)),
                ("Revision", FormatRevision(fourthRevision))));
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.Equal("/Announcements?notice=deleted", deleted.Headers.Location?.OriginalString);

        await using AsyncServiceScope verification = factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext db = verification.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.False(await db.Announcements.AnyAsync(value => value.Id == announcementId));
        Assert.Equal(
            5,
            await db.AuditRecords.CountAsync(
                value => value.EventCode.StartsWith("announcement.")));
        Assert.All(
            await db.AuditRecords
                .Where(value => value.EventCode.StartsWith("announcement."))
                .ToArrayAsync(),
            audit => Assert.Equal(ownerId, audit.ActorUserId));
    }

    [Fact]
    public async Task PoliciesEnforceOwnerAdminEditorViewerAnonymousDisabledAndStaleSessions()
    {
        await using CreatorToolkitWebFactory factory = new();
        Guid ownerId = await InitializeOwnerAsync(factory.Services);
        CreatedUser admin = await CreateAndActivateAsync(
            factory.Services,
            ownerId,
            "announcement-admin",
            SystemRoles.Admin);
        CreatedUser editor = await CreateAndActivateAsync(
            factory.Services,
            ownerId,
            "announcement-editor",
            SystemRoles.Editor);
        CreatedUser viewer = await CreateAndActivateAsync(
            factory.Services,
            ownerId,
            "announcement-viewer",
            SystemRoles.Viewer);
        Guid announcementId = await CreateDraftAsync(factory.Services, ownerId);

        using HttpClient ownerClient = CreateClient(factory);
        using HttpClient adminClient = CreateClient(factory);
        using HttpClient editorClient = CreateClient(factory);
        using HttpClient viewerClient = CreateClient(factory);
        using HttpClient anonymousClient = CreateClient(factory);
        await LoginAsync(ownerClient, "owner-local", OwnerPassword);
        await LoginAsync(adminClient, "announcement-admin", UserPassword);
        await LoginAsync(editorClient, "announcement-editor", UserPassword);
        await LoginAsync(viewerClient, "announcement-viewer", UserPassword);

        foreach (HttpClient reader in new[] { ownerClient, adminClient, editorClient, viewerClient })
        {
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/Announcements")).StatusCode);
            Assert.Equal(
                HttpStatusCode.OK,
                (await reader.GetAsync($"/Announcements/{announcementId}")).StatusCode);
        }

        foreach (HttpClient editorClientUnderTest in new[] { ownerClient, adminClient, editorClient })
        {
            Assert.Equal(
                HttpStatusCode.OK,
                (await editorClientUnderTest.GetAsync("/Announcements/New")).StatusCode);
            Assert.Equal(
                HttpStatusCode.OK,
                (await editorClientUnderTest.GetAsync(
                    $"/Announcements/{announcementId}/Edit")).StatusCode);
            Assert.Equal(
                HttpStatusCode.OK,
                (await editorClientUnderTest.GetAsync(
                    $"/Announcements/{announcementId}/Delete")).StatusCode);
        }

        AssertAccessDenied(await viewerClient.GetAsync("/Announcements/New"));
        AssertAccessDenied(
            await viewerClient.GetAsync($"/Announcements/{announcementId}/Edit"));
        AssertAccessDenied(
            await viewerClient.GetAsync($"/Announcements/{announcementId}/Delete"));
        string viewerFormPage = await viewerClient.GetStringAsync("/ChangePassword");
        AssertAccessDenied(
            await viewerClient.PostAsync(
                $"/Announcements/{announcementId}/Archive",
                Form(
                    ("__RequestVerificationToken", GetAntiforgeryToken(viewerFormPage)),
                    ("Revision", "1"))));
        string viewerToken = GetAntiforgeryToken(viewerFormPage);
        foreach ((string route, FormUrlEncodedContent form) in new[]
        {
            (
                "/Announcements/New",
                Form(
                    ("__RequestVerificationToken", viewerToken),
                    ("AnnouncementId", Guid.NewGuid().ToString()),
                    ("Title", "Rejected viewer draft"),
                    ("MessageContent", "Rejected viewer body"))),
            (
                $"/Announcements/{announcementId}/Edit",
                Form(
                    ("__RequestVerificationToken", viewerToken),
                    ("Revision", "1"),
                    ("Title", "Rejected viewer edit"),
                    ("MessageContent", "Rejected viewer body"))),
            (
                $"/Announcements/{announcementId}/Restore",
                Form(
                    ("__RequestVerificationToken", viewerToken),
                    ("Revision", "1"))),
            (
                $"/Announcements/{announcementId}/Delete",
                Form(
                    ("__RequestVerificationToken", viewerToken),
                    ("Revision", "1"))),
        })
        {
            using (form)
            {
                AssertAccessDenied(await viewerClient.PostAsync(route, form));
            }
        }

        AssertLoginRedirect(await anonymousClient.GetAsync("/Announcements"));
        AssertLoginRedirect(
            await anonymousClient.GetAsync($"/Announcements/{announcementId}"));
        AssertLoginRedirect(await anonymousClient.GetAsync("/Announcements/New"));

        string viewerStamp = await GetConcurrencyStampAsync(factory.Services, viewer.Id);
        await using (AsyncServiceScope disableScope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(
                UserLifecycleStatus.Succeeded,
                (await disableScope.ServiceProvider
                    .GetRequiredService<UserLifecycleService>()
                    .DisableAsync(ownerId, viewer.Id, viewerStamp)).Status);
        }

        AssertLoginRedirect(await viewerClient.GetAsync("/Announcements"));

        await using (AsyncServiceScope stampScope = factory.Services.CreateAsyncScope())
        {
            UserManager<ApplicationUser> users = stampScope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser staleUser = (await users.FindByIdAsync(editor.Id.ToString()))!;
            Assert.True((await users.UpdateSecurityStampAsync(staleUser)).Succeeded);
        }

        AssertLoginRedirect(await editorClient.GetAsync("/Announcements"));

        await using AsyncServiceScope verification = factory.Services.CreateAsyncScope();
        Announcement stored = await verification.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .Announcements
            .AsNoTracking()
            .SingleAsync(value => value.Id == announcementId);
        Assert.Equal(AnnouncementStatus.Draft, stored.Status);
        Assert.Equal(1, stored.Revision);
        _ = admin;
    }

    [Theory]
    [InlineData(SystemRoles.Admin, "announcement-admin-author")]
    [InlineData(SystemRoles.Editor, "announcement-editor-author")]
    public async Task ContentEditingRolesCanPerformEveryAnnouncementMutation(
        string role,
        string userName)
    {
        await using CreatorToolkitWebFactory factory = new();
        Guid ownerId = await InitializeOwnerAsync(factory.Services);
        CreatedUser actor = await CreateAndActivateAsync(
            factory.Services,
            ownerId,
            userName,
            role);
        using HttpClient client = CreateClient(factory);
        await LoginAsync(client, userName, UserPassword);

        string newHtml = await client.GetStringAsync("/Announcements/New");
        Guid announcementId = Guid.Parse(GetHiddenValue(newHtml, "AnnouncementId"));
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await client.PostAsync(
                "/Announcements/New",
                Form(
                    ("__RequestVerificationToken", GetAntiforgeryToken(newHtml)),
                    ("AnnouncementId", announcementId.ToString()),
                    ("Title", "Role authorization draft"),
                    ("MessageContent", "Role authorization body")))).StatusCode);

        string editHtml = await client.GetStringAsync($"/Announcements/{announcementId}/Edit");
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await client.PostAsync(
                $"/Announcements/{announcementId}/Edit",
                Form(
                    ("__RequestVerificationToken", GetAntiforgeryToken(editHtml)),
                    ("Revision", GetHiddenValue(editHtml, "Revision")),
                    ("Title", "Role authorization edit"),
                    ("MessageContent", "Role authorization edited body")))).StatusCode);

        string detailsHtml = await client.GetStringAsync($"/Announcements/{announcementId}");
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await client.PostAsync(
                $"/Announcements/{announcementId}/Archive",
                Form(
                    ("__RequestVerificationToken", GetAntiforgeryToken(detailsHtml)),
                    ("Revision", GetHiddenValue(detailsHtml, "Revision"))))).StatusCode);

        detailsHtml = await client.GetStringAsync($"/Announcements/{announcementId}");
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await client.PostAsync(
                $"/Announcements/{announcementId}/Restore",
                Form(
                    ("__RequestVerificationToken", GetAntiforgeryToken(detailsHtml)),
                    ("Revision", GetHiddenValue(detailsHtml, "Revision"))))).StatusCode);

        string deleteHtml = await client.GetStringAsync(
            $"/Announcements/{announcementId}/Delete");
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await client.PostAsync(
                $"/Announcements/{announcementId}/Delete",
                Form(
                    ("__RequestVerificationToken", GetAntiforgeryToken(deleteHtml)),
                    ("Revision", GetHiddenValue(deleteHtml, "Revision"))))).StatusCode);

        await using AsyncServiceScope verification = factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext db = verification.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Assert.False(await db.Announcements.AnyAsync(value => value.Id == announcementId));
        Assert.Equal(
            5,
            await db.AuditRecords.CountAsync(
                value => value.ActorUserId == actor.Id
                    && value.EventCode.StartsWith("announcement.")));
    }

    [Fact]
    public async Task AntiforgeryGetSafetyAndDuplicateSubmissionRemainBounded()
    {
        await using CreatorToolkitWebFactory factory = new();
        using HttpClient client = CreateClient(factory);
        Guid ownerId = await InitializeOwnerAsync(factory.Services);
        await LoginAsync(client, "owner-local", OwnerPassword);
        Guid announcementId = await CreateDraftAsync(factory.Services, ownerId);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PostAsync(
                $"/Announcements/{announcementId}/Archive",
                Form(("Revision", "1")))).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/Announcements/{announcementId}/Archive")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/Announcements/{announcementId}/Restore")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync($"/Announcements/{announcementId}/Delete")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync($"/Announcements/{announcementId}/Edit")).StatusCode);

        string newHtml = await client.GetStringAsync("/Announcements/New");
        Guid submissionId = Guid.Parse(GetHiddenValue(newHtml, "AnnouncementId"));
        string antiforgery = GetAntiforgeryToken(newHtml);
        FormUrlEncodedContent CreateForm() => Form(
            ("__RequestVerificationToken", antiforgery),
            ("AnnouncementId", submissionId.ToString()),
            ("Title", "One submitted draft"),
            ("MessageContent", "One submitted body"));
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await client.PostAsync("/Announcements/New", CreateForm())).StatusCode);
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await client.PostAsync("/Announcements/New", CreateForm())).StatusCode);

        await using AsyncServiceScope verification = factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext db = verification.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Announcement original = await db.Announcements
            .AsNoTracking()
            .SingleAsync(value => value.Id == announcementId);
        Assert.Equal(AnnouncementStatus.Draft, original.Status);
        Assert.Equal(1, original.Revision);
        Assert.Equal(
            1,
            await db.Announcements.CountAsync(value => value.Id == submissionId));
        Assert.Equal(
            2,
            await db.AuditRecords.CountAsync(
                value => value.EventCode == "announcement.created"
                    && value.ActorUserId == ownerId));
    }

    [Fact]
    public async Task StaleEditPreservesEnteredContentWithoutOverwritingOrLeakingCanaries()
    {
        List<string> logs = [];
        await using CreatorToolkitWebFactory factory = new(
            services => services.AddLogging(
                logging => logging.AddProvider(new TestLoggerProvider(logs))));
        using HttpClient client = CreateClient(factory);
        Guid ownerId = await InitializeOwnerAsync(factory.Services);
        await LoginAsync(client, "owner-local", OwnerPassword);
        Guid announcementId = await CreateDraftAsync(factory.Services, ownerId);
        string editHtml = await client.GetStringAsync($"/Announcements/{announcementId}/Edit");
        string antiforgery = GetAntiforgeryToken(editHtml);
        string revision = GetHiddenValue(editHtml, "Revision");

        await using (AsyncServiceScope updateScope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(
                AnnouncementOperationStatus.Succeeded,
                (await updateScope.ServiceProvider
                    .GetRequiredService<IAnnouncementService>()
                    .UpdateAsync(
                        announcementId,
                        "Current title",
                        "Current body",
                        ParseRevision(revision),
                        ownerId)).Status);
        }

        string titleCanary = "stale-title-canary-01d376d2";
        string bodyCanary = "stale-body-canary-cc3f71c4";
        HttpResponseMessage stale = await client.PostAsync(
            $"/Announcements/{announcementId}/Edit",
            Form(
                ("__RequestVerificationToken", antiforgery),
                ("Revision", revision),
                ("Title", titleCanary),
                ("MessageContent", bodyCanary)));
        string staleHtml = await stale.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Contains("changed after you opened it", staleHtml, StringComparison.Ordinal);
        Assert.Contains(titleCanary, staleHtml, StringComparison.Ordinal);
        Assert.Contains(bodyCanary, staleHtml, StringComparison.Ordinal);
        AssertNoStoreAndSecurityHeaders(stale);

        string searchCanary = "search-canary-6236edb7";
        HttpResponseMessage search = await client.GetAsync(
            $"/Announcements?search={Uri.EscapeDataString(searchCanary)}");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);

        await using AsyncServiceScope verification = factory.Services.CreateAsyncScope();
        CreatorToolkitDbContext db = verification.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>();
        Announcement current = await db.Announcements.AsNoTracking().SingleAsync();
        Assert.Equal("Current title", current.Title);
        Assert.Equal("Current body", current.MessageContent);
        Assert.Equal(2, current.Revision);
        Assert.Equal(
            2,
            await db.AuditRecords.CountAsync(
                value => value.EventCode.StartsWith("announcement.")));
        Assert.Equal(0, await db.DiagnosticRecords.CountAsync());

        string unsafeDestinations = string.Join('\n', logs)
            + stale.Headers
            + search.Headers
            + string.Join(
                '|',
                await db.AuditRecords
                    .Select(
                        value => value.EventCode
                            + ":" + value.Outcome
                            + ":" + value.ReasonCode
                            + ":" + value.DiagnosticReference)
                    .ToArrayAsync());
        AssertCanaryAbsent("stale title", titleCanary, unsafeDestinations);
        AssertCanaryAbsent("stale body", bodyCanary, unsafeDestinations);
        AssertCanaryAbsent("search", searchCanary, unsafeDestinations);
    }

    private static async Task<Guid> InitializeOwnerAsync(IServiceProvider services)
    {
        string rawCapability = WebEncoders.Base64UrlEncode(
            SHA256.HashData(Encoding.UTF8.GetBytes("announcement-http-bootstrap")));
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<BootstrapCapabilityIssuer>()
            .IssueAsync(Hash(rawCapability));
        Assert.Equal(
            InitialOwnerSetupStatus.Succeeded,
            (await scope.ServiceProvider
                .GetRequiredService<InitialOwnerSetupService>()
                .CreateAsync(
                    new InitialOwnerSetupRequest(
                        rawCapability,
                        "owner-local",
                        "Owner",
                        OwnerPassword))).Status);
        return await scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .Users
            .Select(value => value.Id)
            .SingleAsync();
    }

    private static async Task<CreatedUser> CreateAndActivateAsync(
        IServiceProvider services,
        Guid actorId,
        string userName,
        string role)
    {
        await using AsyncServiceScope createScope = services.CreateAsyncScope();
        UserLifecycleResult pending = await createScope.ServiceProvider
            .GetRequiredService<UserLifecycleService>()
            .CreatePendingAsync(actorId, userName, null, role);
        Assert.Equal(UserLifecycleStatus.Succeeded, pending.Status);

        await using AsyncServiceScope activationScope = services.CreateAsyncScope();
        Assert.Equal(
            AccountActivationStatus.Succeeded,
            (await activationScope.ServiceProvider
                .GetRequiredService<AccountActivationService>()
                .ActivateAsync(pending.OneTimeActivationCapability!, UserPassword)).Status);
        return new CreatedUser(pending.TargetUserId!.Value);
    }

    private static async Task<Guid> CreateDraftAsync(
        IServiceProvider services,
        Guid actorId)
    {
        Guid id = Guid.NewGuid();
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        Assert.Equal(
            AnnouncementOperationStatus.Succeeded,
            (await scope.ServiceProvider
                .GetRequiredService<IAnnouncementService>()
                .CreateAsync(id, "Authorization draft", "Authorization body", actorId)).Status);
        return id;
    }

    private static async Task LoginAsync(
        HttpClient client,
        string userName,
        string password)
    {
        string html = await client.GetStringAsync("/Login");
        HttpResponseMessage response = await client.PostAsync(
            "/Login",
            Form(
                ("__RequestVerificationToken", GetAntiforgeryToken(html)),
                ("UserName", userName),
                ("Password", password)));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> GetConcurrencyStampAsync(
        IServiceProvider services,
        Guid userId)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<CreatorToolkitDbContext>()
            .Users
            .Where(value => value.Id == userId)
            .Select(value => value.ConcurrencyStamp!)
            .SingleAsync();
    }

    private static HttpClient CreateClient(CreatorToolkitWebFactory factory)
    {
        return factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
    }

    private static FormUrlEncodedContent Form(
        params (string Name, string Value)[] values)
    {
        return new FormUrlEncodedContent(
            values.Select(
                value => new KeyValuePair<string, string>(value.Name, value.Value)));
    }

    private static void AddField(MultipartFormDataContent form, string name, string value) =>
        form.Add(new StringContent(value), name);

    private static string GetAntiforgeryToken(string html)
    {
        Match match = AntiforgeryTokenPattern().Match(html);
        Assert.True(match.Success);
        return match.Groups[1].Value;
    }

    private static string GetHiddenValue(string html, string name)
    {
        Match match = Regex.Match(
            html,
            $"<input(?=[^>]*name=\"{Regex.Escape(name)}\")(?=[^>]*value=\"([^\"]+)\")[^>]*>",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static void AssertNoStoreAndSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
        Assert.True(response.Headers.Contains("Referrer-Policy"));
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
    }

    private static void AssertAccessDenied(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/AccessDenied", response.Headers.Location?.AbsolutePath);
    }

    private static void AssertLoginRedirect(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Login", response.Headers.Location?.AbsolutePath);
    }

    private static void AssertCanaryAbsent(
        string category,
        string canary,
        string destination)
    {
        Assert.True(
            !destination.Contains(canary, StringComparison.Ordinal),
            $"The {category} canary appeared in an unsafe destination.");
    }

    private static byte[] Hash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static long ParseRevision(string value) =>
        long.Parse(value, CultureInfo.InvariantCulture);

    private static string FormatRevision(long value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private sealed record CreatedUser(Guid Id);

    [GeneratedRegex(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryTokenPattern();
}
