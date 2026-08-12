(() => {
    async function post(port, path, body) {
        try {
            const response = await fetch(`http://127.0.0.1:${port}${path}`, {
                method: "POST",
                headers: { "Content-Type": "application/json; charset=UTF-8" },
                body: JSON.stringify(body)
            });
            if (!response.ok) {
                console.warn(`[Sonoran Studio] ${path} rejected a message with status ${response.status}.`);
            }
        } catch {
            // The Sonoran Studio desktop companion is optional, so an unavailable local endpoint is expected.
        }
    }

    window.addEventListener("message", (message) => {
        const data = message.data || {};
        if (data.type === "studio_lighting") {
            void post(data.port, "/lighting", { state: data.state });
        } else if (data.type === "studio_game_event") {
            void post(data.port, "/fivem", { event: data.event, args: data.args || {} });
        }
    });
})();
