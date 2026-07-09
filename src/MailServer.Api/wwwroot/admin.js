window.mailserverDropdowns = (() => {
    const entries = new Map();
    let nextId = 1;
    let listening = false;

    const eventPathContains = (event, element) => {
        if (!element) {
            return false;
        }

        const path = typeof event.composedPath === "function" ? event.composedPath() : null;
        return path ? path.includes(element) : element.contains(event.target);
    };

    const removeMissingElements = () => {
        for (const [id, entry] of entries) {
            if (!entry.element || !document.documentElement.contains(entry.element)) {
                entries.delete(id);
            }
        }
    };

    const closeEntry = entry => {
        entry.dotNetRef?.invokeMethodAsync("CloseFromOutsideAsync").catch(() => {
            // The Blazor circuit may have disconnected after registration.
        });
    };

    const closeOutside = event => {
        removeMissingElements();

        for (const [id, entry] of [...entries]) {
            if (eventPathContains(event, entry.element)) {
                continue;
            }

            entries.delete(id);
            closeEntry(entry);
        }
    };

    const closeAll = () => {
        removeMissingElements();

        for (const [id, entry] of [...entries]) {
            entries.delete(id);
            closeEntry(entry);
        }
    };

    const ensureListeners = () => {
        if (listening) {
            return;
        }

        document.addEventListener("pointerdown", closeOutside, true);
        document.addEventListener("keydown", event => {
            if (event.key === "Escape") {
                closeAll();
            }
        }, true);
        listening = true;
    };

    return {
        register: (element, dotNetRef) => {
            ensureListeners();
            if (!element) {
                return "";
            }

            const id = element.dataset.mailserverDropdownId || String(nextId++);
            element.dataset.mailserverDropdownId = id;
            entries.set(id, { element, dotNetRef });
            return id;
        },
        unregister: id => {
            if (id != null) {
                entries.delete(String(id));
            }
        },
        closeAll
    };
})();

window.mailserverTabs = (() => {
    const selectorEscape = value => window.CSS?.escape ? CSS.escape(value) : String(value).replace(/"/g, "\\\"");

    const applyState = (group, value) => {
        const escapedGroup = selectorEscape(group);
        const buttons = document.querySelectorAll(`[data-admin-tab-button="${escapedGroup}"]`);
        const panels = document.querySelectorAll(`[data-admin-tab-panel="${escapedGroup}"]`);

        buttons.forEach(button => {
            const active = button.dataset.tabValue === value;
            button.classList.toggle("active", active);
            button.setAttribute("aria-selected", active ? "true" : "false");
            button.tabIndex = active ? 0 : -1;
        });

        panels.forEach(panel => {
            const active = panel.dataset.tabValue === value;
            panel.hidden = !active;
            panel.classList.toggle("active", active);
        });
    };

    const select = (group, value, updateUrl) => {
        const escapedGroup = selectorEscape(group);
        const escapedValue = selectorEscape(value);
        applyState(group, value);

        if (updateUrl) {
            requestAnimationFrame(() => applyState(group, value));
            window.setTimeout(() => applyState(group, value), 50);
            window.setTimeout(() => applyState(group, value), 250);
        }

        if (!updateUrl) {
            return;
        }

        const activeButton = document.querySelector(`[data-admin-tab-button="${escapedGroup}"][data-tab-value="${escapedValue}"]`);
        const strip = activeButton?.closest("[data-admin-tab-group]");
        const queryParam = strip?.dataset.adminTabQueryParam;
        if (!queryParam || !window.history?.replaceState) {
            return;
        }

        const url = new URL(window.location.href);
        url.searchParams.set(queryParam, value);
        window.history.replaceState(window.history.state, "", url);
    };

    document.addEventListener("click", event => {
        const button = event.target.closest("[data-admin-tab-button]");
        if (!button) {
            return;
        }

        event.preventDefault();
        select(button.dataset.adminTabButton, button.dataset.tabValue, true);
    });

    return { select };
})();

window.mailserverClipboard = {
    copyText: async value => {
        const text = value == null ? "" : String(value);

        if (window.isSecureContext && navigator.clipboard?.writeText) {
            try {
                await navigator.clipboard.writeText(text);
                return true;
            } catch {
                // Some browsers expose the API but still deny it. Fall through.
            }
        }

        const textarea = document.createElement("textarea");
        textarea.value = text;
        textarea.setAttribute("readonly", "");
        textarea.style.position = "fixed";
        textarea.style.left = "-10000px";
        textarea.style.top = "0";
        textarea.style.opacity = "0";
        document.body.appendChild(textarea);
        textarea.focus();
        textarea.select();
        textarea.setSelectionRange(0, textarea.value.length);

        try {
            return document.execCommand("copy");
        } finally {
            textarea.remove();
        }
    }
};
