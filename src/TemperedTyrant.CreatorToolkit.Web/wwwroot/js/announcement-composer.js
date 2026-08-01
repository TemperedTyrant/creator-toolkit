(() => {
    "use strict";

    const wrappers = {
        bold: ["**", "**", "bold text"], italic: ["*", "*", "italic text"],
        underline: ["__", "__", "underlined text"], strike: ["~~", "~~", "struck text"],
        code: ["`", "`", "code"], codeblock: ["```\n", "\n```", "code"],
        quote: ["> ", "", "quoted text"], link: ["[", "](https://)", "link text"],
        spoiler: ["||", "||", "spoiler text"]
    };

    for (const root of document.querySelectorAll("[data-announcement-composer]")) {
        const textarea = root.querySelector("textarea[name='MessageContent']");
        const count = root.querySelector(".character-count");
        const fileInput = root.querySelector("input[name='NewImages']");
        const oneTimeInput = root.querySelector("input[name='UploadedImage']");
        const oneTimeCard = root.querySelector("[data-one-time-media-card]");
        const oneTimePreview = root.querySelector("[data-one-time-preview]");
        const oneTimeDetail = root.querySelector("[data-one-time-image-detail]");
        const newList = root.querySelector("[data-new-media-list]");
        const mediaList = root.querySelector("[data-media-list]");
        const imagePanel = root.querySelector("[data-image-panel]");
        const objectUrls = new Set();
        if (!textarea || !count) continue;

        const updateCount = () => {
            count.textContent = `${Array.from(textarea.value).length.toLocaleString()} / 10,000`;
        };
        const applyMarkdown = command => {
            const rule = wrappers[command];
            if (!rule) return;
            const start = textarea.selectionStart;
            const end = textarea.selectionEnd;
            const selected = textarea.value.slice(start, end) || rule[2];
            textarea.focus();
            textarea.setRangeText(rule[0] + selected + rule[1], start, end, "select");
            textarea.setSelectionRange(start + rule[0].length, start + rule[0].length + selected.length);
            textarea.dispatchEvent(new Event("input", { bubbles: true }));
        };
        for (const button of root.querySelectorAll("[data-markdown-command]")) {
            button.addEventListener("mousedown", event => event.preventDefault());
            button.addEventListener("click", () => applyMarkdown(button.dataset.markdownCommand));
        }
        const imageTrigger = root.querySelector("[data-image-trigger]");
        imageTrigger?.addEventListener("click", () => {
            imagePanel.hidden = !imagePanel.hidden;
            imageTrigger.setAttribute("aria-expanded", String(!imagePanel.hidden));
            if (!imagePanel.hidden) imagePanel.focus();
        });
        root.querySelector("[data-image-close]")?.addEventListener("click", () => {
            imagePanel.hidden = true;
            imageTrigger?.setAttribute("aria-expanded", "false");
            imageTrigger?.focus();
        });
        const form = root.closest("form");
        const mentionPanel = form?.querySelector("[data-mention-panel]");
        const mentionTrigger = root.querySelector("[data-mention-trigger]");
        if (mentionPanel && mentionTrigger) {
            mentionTrigger.hidden = false;
            mentionTrigger.addEventListener("click", () => {
                mentionPanel.open = true;
                mentionPanel.querySelector("input, button")?.focus();
            });
        }
        const advancedPanel = form?.querySelector("[data-advanced-panel]");
        const advancedTrigger = root.querySelector("[data-advanced-trigger]");
        if (advancedPanel && advancedTrigger) {
            advancedTrigger.hidden = false;
            advancedTrigger.addEventListener("click", () => {
                advancedPanel.open = true;
                advancedPanel.querySelector("input, button")?.focus();
            });
        }
        const mentionChips = root.querySelector("[data-mention-chips]");
        const renderMentionChips = () => {
            if (!mentionChips || !form) return;
            mentionChips.replaceChildren();
            const selected = [...form.querySelectorAll("input[name='RoleIds']:checked, input[name='UserIds']:checked, input[name='MentionEveryone']:checked, input[name='MentionHere']:checked")];
            for (const input of selected) {
                const chip = document.createElement("span");
                chip.className = "mention-chip";
                chip.textContent = input.name === "MentionEveryone" ? "@everyone"
                    : input.name === "MentionHere" ? "@here"
                    : input.closest("label")?.childNodes[1]?.textContent?.trim() || `@${input.value}`;
                const remove = document.createElement("button");
                remove.type = "button";
                remove.setAttribute("aria-label", `Remove ${chip.textContent} mention`);
                remove.textContent = "×";
                remove.addEventListener("click", () => {
                    input.checked = false;
                    renderMentionChips();
                    mentionTrigger?.focus();
                });
                chip.append(remove);
                mentionChips.append(chip);
            }
        };
        form?.addEventListener("change", event => {
            if (event.target.matches("input[name='RoleIds'], input[name='UserIds'], input[name='MentionEveryone'], input[name='MentionHere']")) {
                renderMentionChips();
            }
        });

        const updateOrders = () => {
            const cards = [...mediaList.querySelectorAll(".media-card")]
                .filter(card => !card.querySelector("input[name$='.Remove']")?.checked);
            cards.forEach((card, index) => {
                card.dataset.sortOrder = String(index);
                const order = card.querySelector("[data-media-order]");
                if (order) order.value = String(index);
            });
        };
        const move = (card, direction) => {
            const sibling = direction === "left" ? card.previousElementSibling : card.nextElementSibling;
            if (!sibling?.classList.contains("media-card")) return;
            if (direction === "left") card.parentNode.insertBefore(card, sibling);
            else card.parentNode.insertBefore(sibling, card);
            updateOrders();
            card.querySelector("button[data-move-media]")?.focus();
        };
        mediaList?.addEventListener("click", event => {
            const button = event.target.closest("button[data-move-media]");
            if (button) move(button.closest(".media-card"), button.dataset.moveMedia);
        });
        const enforceFeatured = selected => {
            if (selected.value !== "FeaturedImage") return;
            for (const other of mediaList.querySelectorAll("select[name$='.Presentation']")) {
                if (other !== selected) other.value = "Attachment";
            }
        };
        mediaList?.addEventListener("change", event => {
            if (event.target.matches("select[name$='.Presentation']")) enforceFeatured(event.target);
            if (event.target.matches("input[name$='.Remove']")) {
                event.target.closest(".media-card").classList.toggle("pending-removal", event.target.checked);
                updateOrders();
            }
        });

        const rebuildFileList = removedIndex => {
            if (!fileInput || typeof DataTransfer === "undefined") return;
            const transfer = new DataTransfer();
            [...fileInput.files].forEach((file, index) => {
                if (index !== removedIndex) transfer.items.add(file);
            });
            fileInput.files = transfer.files;
            renderNewFiles();
        };
        const createSafePreviewUrl = async file => {
            const bitmap = await createImageBitmap(file);
            try {
                const canvas = document.createElement("canvas");
                canvas.width = bitmap.width;
                canvas.height = bitmap.height;
                canvas.getContext("2d").drawImage(bitmap, 0, 0);
                const previewBlob = await new Promise((resolve, reject) => {
                    canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error("Image preview unavailable.")), "image/png");
                });
                return URL.createObjectURL(previewBlob);
            } finally {
                bitmap.close();
            }
        };
        const renderNewFiles = () => {
            if (!fileInput || !newList) return;
            for (const url of objectUrls) URL.revokeObjectURL(url);
            objectUrls.clear();
            newList.replaceChildren();
            [...fileInput.files].slice(0, 4).forEach((file, index) => {
                const card = document.createElement("article");
                card.className = "media-card unsaved-media";
                const preview = document.createElement("img");
                preview.alt = "Unsaved image preview";
                createSafePreviewUrl(file).then(objectUrl => {
                    if (!fileInput.files[index] || fileInput.files[index] !== file) {
                        URL.revokeObjectURL(objectUrl);
                        return;
                    }
                    objectUrls.add(objectUrl);
                    preview.src = objectUrl;
                }).catch(() => {
                    preview.remove();
                });
                const fields = document.createElement("div");
                fields.className = "media-card-fields";
                const heading = document.createElement("strong");
                heading.textContent = "Unsaved image";
                const detail = document.createElement("span");
                detail.className = "muted";
                detail.textContent = `${file.type || "image"} · ${file.size.toLocaleString()} bytes`;
                const altLabel = document.createElement("label"); altLabel.textContent = "Alt text";
                const alt = document.createElement("input"); alt.name = `NewImageAltTexts[${index}]`; alt.maxLength = 1024;
                const spoilerLabel = document.createElement("label"); spoilerLabel.className = "choice";
                const spoiler = document.createElement("input"); spoiler.type = "checkbox"; spoiler.name = `NewImageSpoilers[${index}]`; spoiler.value = "true";
                const spoilerFalse = document.createElement("input"); spoilerFalse.type = "hidden"; spoilerFalse.name = spoiler.name; spoilerFalse.value = "false";
                spoilerLabel.append(spoiler, document.createTextNode(" Spoiler"), spoilerFalse);
                const presentationLabel = document.createElement("label"); presentationLabel.textContent = "Presentation";
                const presentation = document.createElement("select"); presentation.name = `NewImagePresentations[${index}]`;
                presentation.append(new Option("Attachment", "Attachment"), new Option("Featured image", "FeaturedImage"));
                presentation.addEventListener("change", () => enforceFeatured(presentation));
                const order = document.createElement("input"); order.type = "hidden"; order.name = `NewImageSortOrders[${index}]`; order.dataset.mediaOrder = "";
                const actions = document.createElement("div"); actions.className = "media-card-actions";
                const left = document.createElement("button"); left.type = "button"; left.className = "secondary"; left.dataset.moveMedia = "left"; left.textContent = "Move left";
                const right = document.createElement("button"); right.type = "button"; right.className = "secondary"; right.dataset.moveMedia = "right"; right.textContent = "Move right";
                const remove = document.createElement("button"); remove.type = "button"; remove.className = "secondary danger-text"; remove.textContent = "Remove";
                remove.setAttribute("aria-label", `Remove unsaved image ${index + 1}`);
                remove.addEventListener("click", () => rebuildFileList(index));
                actions.append(left, right, remove);
                fields.append(heading, detail, altLabel, alt, spoilerLabel, presentationLabel, presentation, order, actions);
                card.append(preview, fields); newList.append(card);
            });
            updateOrders();
        };
        const clearOneTimeImage = () => {
            if (!oneTimeInput || !oneTimeCard) return;
            oneTimeInput.value = "";
            oneTimeCard.hidden = true;
            if (oneTimePreview) oneTimePreview.removeAttribute("src");
            for (const url of objectUrls) URL.revokeObjectURL(url);
            objectUrls.clear();
            oneTimeInput.focus();
        };
        const renderOneTimeImage = () => {
            if (!oneTimeInput || !oneTimeCard) return;
            for (const url of objectUrls) URL.revokeObjectURL(url);
            objectUrls.clear();
            const file = oneTimeInput.files[0];
            oneTimeCard.hidden = !file;
            if (!file) return;
            if (oneTimeDetail) {
                oneTimeDetail.textContent = `${file.type || "image"} · ${file.size.toLocaleString()} bytes · Used for this publication only`;
            }
            if (oneTimePreview) {
                createSafePreviewUrl(file).then(objectUrl => {
                    if (oneTimeInput.files[0] !== file) {
                        URL.revokeObjectURL(objectUrl);
                        return;
                    }
                    objectUrls.add(objectUrl);
                    oneTimePreview.src = objectUrl;
                }).catch(() => oneTimePreview.removeAttribute("src"));
            }
        };
        fileInput?.addEventListener("change", renderNewFiles);
        oneTimeInput?.addEventListener("change", renderOneTimeImage);
        root.querySelector("[data-remove-one-time-image]")?.addEventListener("click", clearOneTimeImage);
        textarea.addEventListener("input", updateCount);
        window.addEventListener("pagehide", () => {
            for (const url of objectUrls) URL.revokeObjectURL(url);
        }, { once: true });
        updateCount(); updateOrders(); renderMentionChips();
    }
})();
