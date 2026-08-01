(() => {
    "use strict";

    const form = document.getElementById("discord-publication-form");
    if (!form) return;

    const summary = form.querySelector(".validation-summary");
    const confirmation = form.querySelector("[data-review-confirmation]");
    const firstReview = form.querySelector("[data-review-publication]");
    const reviewMedia = form.querySelector("[data-review-media]");
    const reviewedCount = form.querySelector("[data-reviewed-image-count]");
    const reviewedPresentation = form.querySelector("[data-reviewed-presentation]");
    const upload = form.querySelector("input[name='UploadedImage']");
    const complete = form.querySelector("input[name='ReviewComplete']");
    const token = form.querySelector("input[name='ReviewToken']");
    const finalConfirmation = form.querySelector("input[name='FinalConfirmation']");
    let reviewing = false;
    let formGeneration = 0;

    const setErrors = errors => {
        summary.replaceChildren();
        if (!Array.isArray(errors) || errors.length === 0) return;
        const list = document.createElement("ul");
        for (const error of errors) {
            const item = document.createElement("li");
            item.textContent = String(error);
            list.append(item);
        }
        summary.append(list);
        summary.focus();
    };

    const addText = (parent, value, className) => {
        const text = document.createElement("p");
        if (className) text.className = className;
        text.textContent = value;
        parent.append(text);
    };

    const renderReviewedMedia = payload => {
        reviewMedia.replaceChildren();
        reviewMedia.hidden = false;
        const stored = Array.isArray(payload.storedImages) ? payload.storedImages : [];
        for (const media of stored) {
            const card = document.createElement("article");
            card.className = "review-media-card";
            const selectedInput = [...form.querySelectorAll("input[name='SelectedMediaIds']")]
                .find(input => input.value === media.id);
            const savedPreview = selectedInput?.closest("[data-saved-media]")?.querySelector("img");
            if (savedPreview) {
                const image = savedPreview.cloneNode();
                image.alt = media.altText || "";
                image.loading = "lazy";
                card.append(image);
            }
            const details = document.createElement("div");
            const heading = document.createElement("strong");
            const format = String(media.contentType || "image").split("/").pop().toUpperCase();
            heading.textContent = `Stored ${format} image`;
            details.append(heading);
            addText(details, `${Number(media.byteLength).toLocaleString()} bytes · ${media.featured ? "Featured" : "Attachment"}${media.spoiler ? " · Spoiler" : ""}`);
            addText(details, media.altText ? `Alt text: ${media.altText}` : "No alt text");
            card.append(details);
            reviewMedia.append(card);
        }

        if (payload.oneTimeImage) {
            const card = document.createElement("article");
            card.className = "review-media-card";
            const localPreview = form.querySelector("[data-one-time-preview][src]");
            if (localPreview) {
                const image = localPreview.cloneNode();
                image.alt = "One-time image preview";
                card.append(image);
            }
            const details = document.createElement("div");
            const heading = document.createElement("strong");
            heading.textContent = `One-time ${payload.oneTimeImage.format} image`;
            details.append(heading);
            addText(details, `${Number(payload.oneTimeImage.byteSize).toLocaleString()} bytes · ${payload.oneTimeImage.featured ? "Featured" : "Attachment"}${payload.oneTimeImage.spoiler ? " · Spoiler" : ""}`);
            addText(details, `${payload.oneTimeImage.hasAltText ? "Alt text provided" : "No alt text"} · Used for this publication only`);
            card.append(details);
            reviewMedia.append(card);
        }
        if (reviewedCount) {
            reviewedCount.textContent = `${stored.length} selected`;
        }
        if (reviewedPresentation) {
            reviewedPresentation.textContent = form.elements.namedItem("Mode")?.value || "Plain";
        }
    };

    const invalidateReview = () => {
        if (reviewing || complete?.value.toLowerCase() !== "true") return;
        complete.value = "false";
        token.value = "";
        if (finalConfirmation) finalConfirmation.checked = false;
        if (upload) upload.disabled = false;
        if (confirmation) confirmation.hidden = true;
        if (firstReview) firstReview.hidden = false;
    };

    form.addEventListener("input", event => {
        if (event.target !== finalConfirmation) {
            formGeneration++;
            invalidateReview();
        }
    });
    form.addEventListener("change", event => {
        if (event.target !== finalConfirmation) {
            formGeneration++;
            invalidateReview();
        }
    });

    form.addEventListener("click", async event => {
        const button = event.target.closest("[data-review-publication]");
        if (!button) return;
        event.preventDefault();
        if (reviewing) return;
        reviewing = true;
        const reviewedGeneration = formGeneration;
        button.disabled = true;
        setErrors([]);
        try {
            const response = await fetch(button.formAction, {
                method: "POST",
                body: new FormData(form),
                credentials: "same-origin",
                redirect: "error",
                headers: {
                    "Accept": "application/json",
                    "X-Creator-Toolkit-Partial": "publication-review"
                }
            });
            const payload = await response.json();
            if (reviewedGeneration !== formGeneration) {
                setErrors(["The publication changed while it was being reviewed. Review it again."]);
                return;
            }
            setErrors(payload.errors);
            if (!response.ok || payload.status !== "ok") return;
            complete.value = "true";
            token.value = payload.reviewToken;
            if (finalConfirmation) finalConfirmation.checked = false;
            if (upload?.files.length) upload.disabled = true;
            renderReviewedMedia(payload);
            firstReview.hidden = true;
            confirmation.hidden = false;
            confirmation.querySelector("input, button")?.focus();
        } catch {
            setErrors(["The publication could not be reviewed. Try again."]);
        } finally {
            reviewing = false;
            button.disabled = false;
        }
    });
})();
