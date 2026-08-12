(() => {
    const childProcess = require("child_process");
    const path = require("path");

    function runWorker(payload, completed) {
        const workerPath = path.join(GetResourcePath(GetCurrentResourceName()), "server", "unzip-child.js");
        let worker;
        let finished = false;
        let timeout;

        const finish = (success, error) => {
            if (finished) return;
            finished = true;
            if (timeout) clearTimeout(timeout);
            completed(success, error);
        };

        try {
            worker = childProcess.fork(workerPath, [], {
                windowsHide: true,
                stdio: ["ignore", "pipe", "pipe", "ipc"]
            });
            worker.once("message", (message) => finish(Boolean(message && message.ok), message && message.error));
            worker.once("error", (error) => finish(false, error.message));
            worker.once("exit", (code, signal) => {
                if (finished) return;
                const detail = signal ? `signal ${signal}` : `code ${code}`;
                finish(false, `Updater worker exited with ${detail}.`);
            });
            if (payload.probe) {
                timeout = setTimeout(() => {
                    try { worker.kill(); } catch (_) {}
                    finish(false, "Updater permission check timed out.");
                }, 10000);
            }
            worker.send(payload);
        } catch (error) {
            finish(false, error.message);
        }
    }

    exports("CheckUpdaterPermissions", () => {
        runWorker({ probe: true }, (success, error) => emit("sonoranStudioUpdaterPermissionChecked", success, error));
    });

    exports("UnzipUpdate", (file, destination) => {
        runWorker({ file, destination }, (success, error) => emit("sonoranStudioUpdateExtracted", success, error));
    });
})();
