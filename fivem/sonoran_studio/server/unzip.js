(() => {
    const childProcess = require("child_process");
    const path = require("path");

    exports("UnzipUpdate", (file, destination) => {
        const workerPath = path.join(GetResourcePath(GetCurrentResourceName()), "server", "unzip-child.js");
        const worker = childProcess.fork(workerPath, [], {
            windowsHide: true,
            stdio: ["ignore", "pipe", "pipe", "ipc"]
        });
        let finished = false;

        const finish = (success, error) => {
            if (finished) return;
            finished = true;
            emit("sonoranStudioUpdateExtracted", success, error);
        };

        worker.once("message", (message) => finish(Boolean(message && message.ok), message && message.error));
        worker.once("error", (error) => finish(false, error.message));
        worker.once("exit", (code, signal) => {
            if (finished) return;
            const detail = signal ? `signal ${signal}` : `code ${code}`;
            finish(false, `Unzip worker exited with ${detail}.`);
        });
        worker.send({ file, destination });
    });
})();
