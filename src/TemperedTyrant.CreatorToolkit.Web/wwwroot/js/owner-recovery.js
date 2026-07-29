(() => {
    "use strict";

    const fragment = window.location.hash;
    let capability = null;
    if (fragment.length > 1) {
        const parameters = new URLSearchParams(fragment.substring(1));
        capability = parameters.get("token");
    }

    window.history.replaceState(null, document.title, window.location.pathname);

    const capabilityInput = document.getElementById("Capability");
    const populatedFromFragment = Boolean(capability);
    if (capability && capabilityInput) {
        capabilityInput.value = capability;
    }

    window.addEventListener("pageshow", event => {
        if ((event.persisted || !populatedFromFragment) && capabilityInput) {
            capabilityInput.value = "";
        }
    });
})();
