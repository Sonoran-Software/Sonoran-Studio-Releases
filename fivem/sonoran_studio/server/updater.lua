local resourceName = GetCurrentResourceName()
local helperName = "sonoran_studio_updatehelper"
local helperSignal = "sonoran_studio_updatehelper_action"
local versionUrl = "https://raw.githubusercontent.com/Sonoran-Software/Sonoran-Studio-Releases/master/fivem/sonoran_studio/version.json"
local downloadUrl = "https://github.com/Sonoran-Software/Sonoran-Studio-Releases/releases/download/fivem-latest/Sonoran-Studio-FiveM.zip"
local checkInterval = 60 * 60 * 1000
local updateInProgress = false

local function log(message)
    print(("[Sonoran Studio] %s"):format(message))
end

local function versionParts(version)
    local major, minor, patch = tostring(version or ""):match("^(%d+)%.(%d+)%.(%d+)$")
    if major == nil then
        return nil
    end
    return { tonumber(major), tonumber(minor), tonumber(patch) }
end

local function isNewerVersion(candidate, current)
    local candidateParts = versionParts(candidate)
    local currentParts = versionParts(current)
    if candidateParts == nil or currentParts == nil then
        return false
    end

    for index = 1, 3 do
        if candidateParts[index] ~= currentParts[index] then
            return candidateParts[index] > currentParts[index]
        end
    end
    return false
end

local function startUpdate(latestVersion)
    updateInProgress = true
    log(("Downloading update %s..."):format(latestVersion))
    PerformHttpRequest(downloadUrl, function(status, data)
        if status ~= 200 or type(data) ~= "string" or data == "" then
            updateInProgress = false
            log(("Automatic update download failed with HTTP %s. The current version will continue running."):format(tostring(status)))
            return
        end

        local updatePath = GetResourcePath(resourceName) .. "/update.zip"
        local file, openError = io.open(updatePath, "wb")
        if file == nil then
            updateInProgress = false
            log(("Automatic update could not write update.zip: %s"):format(tostring(openError)))
            return
        end
        file:write(data)
        file:close()

        local resourcesPath = GetResourcePath(resourceName) .. "/../../"
        exports[resourceName]:UnzipUpdate(updatePath, resourcesPath)
    end, "GET")
end

AddEventHandler("sonoranStudioUpdateExtracted", function(success, errorMessage)
    updateInProgress = false
    local updatePath = GetResourcePath(resourceName) .. "/update.zip"
    os.remove(updatePath)

    if not success then
        log(("Automatic update could not be installed: %s"):format(tostring(errorMessage or "unknown extraction error")))
        return
    end

    log("Automatic update installed. Restarting the resource...")
    SetConvar(helperSignal, "restart")
    Wait(1000)
    ExecuteCommand("ensure " .. helperName)
end)

local function checkForUpdate()
    if updateInProgress then
        return
    end

    PerformHttpRequest(versionUrl, function(status, data)
        if status ~= 200 then
            log(("Automatic update check failed with HTTP %s. The current version will continue running."):format(tostring(status)))
            return
        end

        local decoded = nil
        local parsed, result = pcall(json.decode, data or "")
        if parsed and type(result) == "table" then
            decoded = result
        end

        local latestVersion = decoded and decoded.resource
        local currentVersion = GetResourceMetadata(resourceName, "version", 0)
        if type(latestVersion) ~= "string" or not isNewerVersion(latestVersion, currentVersion) then
            return
        end
        startUpdate(latestVersion)
    end, "GET")
end

CreateThread(function()
    Wait(10000)
    while true do
        checkForUpdate()
        Wait(checkInterval)
    end
end)
