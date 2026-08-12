(() => {
    const fs = require("fs");
    const path = require("path");
    const unzipper = require("unzipper");
    let sentResult = false;

    function withinDirectory(root, candidate) {
        const relative = path.relative(root, candidate);
        return relative === "" || (!relative.startsWith("..") && !path.isAbsolute(relative));
    }

    function sendResult(message, exitCode) {
        if (sentResult) return;
        sentResult = true;
        process.exitCode = exitCode;
        if (typeof process.send === "function") {
            process.send(message, () => process.disconnect());
        }
    }

    async function extract(file, destination) {
        const root = path.resolve(destination);
        const archive = await unzipper.Open.file(file);

        for (const entry of archive.files) {
            const outputPath = path.resolve(root, entry.path);
            if (!withinDirectory(root, outputPath)) {
                throw new Error(`Update archive contains an invalid path: ${entry.path}`);
            }
            if (entry.type === "Directory") {
                fs.mkdirSync(outputPath, { recursive: true });
                continue;
            }
            fs.mkdirSync(path.dirname(outputPath), { recursive: true });
            await new Promise((resolve, reject) => {
                entry.stream()
                    .once("error", reject)
                    .pipe(fs.createWriteStream(outputPath).once("error", reject).once("finish", resolve));
            });
        }
    }

    process.once("message", ({ file, destination }) => {
        extract(file, destination)
            .then(() => sendResult({ ok: true }, 0))
            .catch((error) => sendResult({ ok: false, error: error.message }, 1));
    });

    process.on("uncaughtException", (error) => sendResult({ ok: false, error: error.message }, 1));
    process.on("unhandledRejection", (error) => sendResult({ ok: false, error: String(error) }, 1));
})();
