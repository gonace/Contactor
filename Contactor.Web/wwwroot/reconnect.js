// The live connection to the app, when it drops: the phone slept, the app was in the background, or the Pi restarted
// after an update. Blazor sets the state on #components-reconnect-modal (the bar in App.razor, which CSS shows per
// state) and reports it here. Nobody should need to understand any of it: a connection that can't be rejoined reloads
// the page by itself.
const bar = document.getElementById("components-reconnect-modal");
const retryButton = document.getElementById("components-reconnect-button");

bar.addEventListener("components-reconnect-state-changed", (event) => {
    switch (event.detail.state) {
        case "failed":
            // Out of attempts, e.g. while the phone had no network: try again as soon as the page is looked at.
            document.addEventListener("visibilitychange", retryWhenVisible);
            break;
        case "rejected":
            // The server no longer knows this page, e.g. after a restart: start afresh.
            location.reload();
            break;
        case "paused":
            // The server paused the page while it was in the background: pick up where it was.
            resume();
            break;
    }
});

retryButton.addEventListener("click", retry);

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenVisible);
    try {
        // true: back; false: the server answered but no longer has this page; an exception: no answer at all.
        if (!(await Blazor.reconnect())) {
            await resume();
        }
    } catch {
        document.addEventListener("visibilitychange", retryWhenVisible);
    }
}

async function resume() {
    try {
        if (!(await Blazor.resumeCircuit())) {
            location.reload();
        }
    } catch {
        location.reload();
    }
}

async function retryWhenVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}
