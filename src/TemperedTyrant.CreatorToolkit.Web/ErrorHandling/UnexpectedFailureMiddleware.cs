using System.Security.Cryptography;
using System.Text.Encodings.Web;
using TemperedTyrant.CreatorToolkit.Core.Diagnostics;

namespace TemperedTyrant.CreatorToolkit.Web.ErrorHandling;

public sealed class UnexpectedFailureMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IDiagnosticRecorder diagnosticRecorder)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(diagnosticRecorder);

        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (BadHttpRequestException exception)
            when (SafeHttpFailureResponses.IsExpected(exception.StatusCode))
        {
            await SafeHttpFailureResponses.WriteAsync(context, exception.StatusCode);
        }
        catch (Exception exception)
        {
            DiagnosticReference? reference;
            try
            {
                reference = await diagnosticRecorder.RecordAsync(
                    new UnexpectedDiagnosticEvent(
                        DiagnosticFailureKind.UnhandledRequest,
                        DiagnosticOperation.HttpRequest,
                        ClassifyException(exception)),
                    CancellationToken.None);
            }
            catch (Exception)
            {
                try
                {
                    reference = DiagnosticReference.CreateRandom();
                }
                catch (Exception)
                {
                    reference = null;
                }
            }

            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";

            string safeReferenceMarkup = reference is null
                ? string.Empty
                : $"<p>Diagnostic reference: <code>{HtmlEncoder.Default.Encode(reference.Value)}</code></p>";
            await context.Response.WriteAsync(
                $"""
                <!doctype html>
                <html lang="en">
                <head><meta charset="utf-8"><title>Unexpected error</title></head>
                <body>
                <main>
                <h1>Something went wrong</h1>
                <p>Try again. If the problem continues, contact the operator.</p>
                {safeReferenceMarkup}
                </main>
                </body>
                </html>
                """);
        }
    }

    private static DiagnosticExceptionType ClassifyException(Exception exception)
    {
        return exception switch
        {
            TimeoutException => DiagnosticExceptionType.Timeout,
            IOException => DiagnosticExceptionType.InputOutput,
            CryptographicException => DiagnosticExceptionType.Cryptography,
            InvalidOperationException => DiagnosticExceptionType.InvalidOperation,
            _ => DiagnosticExceptionType.Unexpected,
        };
    }
}
