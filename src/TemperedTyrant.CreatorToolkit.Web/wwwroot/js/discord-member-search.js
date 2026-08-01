(() => {
    "use strict";

    const form = document.getElementById("discord-publication-form");
    const button = document.getElementById("member-search-button");
    const query = document.getElementById("MemberQuery");
    const results = document.getElementById("member-search-results");
    const resultItems = document.getElementById("member-search-result-items");
    const selectedItems = document.getElementById("selected-discord-members");
    const status = document.getElementById("member-search-status");
    if (!form || !button || !query || !results || !resultItems || !selectedItems || !status) {
        return;
    }

    const selected = new Map();
    let activeSearch = null;
    let generation = 0;

    const addSelected = (id, displayName) => {
        if (!/^\d{1,20}$/.test(id)) {
            return;
        }

        selected.set(id, displayName || id);
        renderSelected();
    };

    const removeSelected = id => {
        selected.delete(id);
        renderSelected();
    };

    const renderSelected = () => {
        selectedItems.replaceChildren();
        for (const [id, displayName] of selected) {
            const row = document.createElement("p");
            row.className = "muted selected-discord-member";
            const input = document.createElement("input");
            input.type = "hidden";
            input.name = "UserIds";
            input.value = id;
            const text = document.createTextNode(`Selected Discord member: ${displayName} (${id}) `);
            const remove = document.createElement("button");
            remove.type = "button";
            remove.className = "link-button";
            remove.textContent = "Remove";
            remove.setAttribute("aria-label", `Remove selected Discord member ${displayName}`);
            remove.addEventListener("click", () => {
                removeSelected(id);
                const choice = resultItems.querySelector(`[data-member-id="${CSS.escape(id)}"]`);
                if (choice) {
                    choice.checked = false;
                }
            });
            row.append(input, text, remove);
            selectedItems.append(row);
        }
    };

    const adoptServerRenderedSelections = () => {
        for (const input of form.querySelectorAll("input[name='UserIds']")) {
            if (/^\d{1,20}$/.test(input.value)) {
                selected.set(input.value, input.value);
            }
        }

        for (const choice of resultItems.querySelectorAll(".member-result-choice")) {
            choice.removeAttribute("name");
            choice.dataset.memberId = choice.value;
            if (choice.checked) {
                selected.set(choice.value, choice.value);
            }
        }

        renderSelected();
    };

    const renderResults = members => {
        resultItems.replaceChildren();
        for (const member of members) {
            if (!member || !/^\d{1,20}$/.test(member.id)) {
                continue;
            }

            const label = document.createElement("label");
            label.className = "choice";
            const choice = document.createElement("input");
            choice.type = "checkbox";
            choice.value = member.id;
            choice.dataset.memberId = member.id;
            choice.checked = selected.has(member.id);
            choice.addEventListener("change", () => {
                if (choice.checked) {
                    addSelected(member.id, member.displayName);
                } else {
                    removeSelected(member.id);
                }
            });
            const detail = document.createElement("span");
            detail.className = "muted";
            detail.textContent = member.id;
            label.append(choice, document.createTextNode(` ${member.displayName} `), detail);
            resultItems.append(label);
        }
    };

    button.addEventListener("click", async event => {
        event.preventDefault();
        const currentGeneration = ++generation;
        if (activeSearch) {
            activeSearch.abort();
        }

        activeSearch = new AbortController();
        results.setAttribute("aria-busy", "true");
        status.textContent = "Searching Discord members…";
        const body = new FormData();
        for (const name of ["__RequestVerificationToken", "Id", "ConnectionId", "GuildId", "MemberQuery"]) {
            const input = form.elements.namedItem(name);
            if (input && "value" in input) {
                body.append(name, input.value);
            }
        }

        try {
            const response = await fetch(form.dataset.memberSearchUrl, {
                method: "POST",
                body,
                credentials: "same-origin",
                redirect: "error",
                headers: {
                    "Accept": "application/json",
                    "X-Creator-Toolkit-Partial": "member-search"
                },
                signal: activeSearch.signal
            });
            const payload = await response.json();
            if (currentGeneration !== generation) {
                return;
            }

            if (!response.ok || payload.status !== "ok") {
                throw new Error("member-search-failed");
            }

            renderResults(Array.isArray(payload.members) ? payload.members : []);
            status.textContent = payload.message;
        } catch (error) {
            if (error.name !== "AbortError" && currentGeneration === generation) {
                resultItems.replaceChildren();
                status.textContent = "Member search is unavailable. Use a Discord user ID instead.";
            }
        } finally {
            if (currentGeneration === generation) {
                results.setAttribute("aria-busy", "false");
                activeSearch = null;
            }
        }
    });

    adoptServerRenderedSelections();
})();
