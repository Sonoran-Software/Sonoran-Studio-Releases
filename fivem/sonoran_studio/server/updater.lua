local resourceName = GetCurrentResourceName()
local helperName = "sonoran_studio_updatehelper"
local helperSignal = "sonoran_studio_updatehelper_action"
local configuredConvar = "sonoran_studio_updater_configured"
local autoUpdateConvar = "sonoran_studio_auto_update"
local versionUrl = "https://raw.githubusercontent.com/Sonoran-Software/Sonoran-Studio-Releases/master/fivem/sonoran_studio/version.json"
local downloadUrl = "https://github.com/Sonoran-Software/Sonoran-Studio-Releases/releases/download/fivem-latest/Sonoran-Studio-FiveM.zip"
local checkInterval = 60 * 60 * 1000
local updateInProgress = false
local updaterReady = false
local permissionCheckComplete = false

local function log(message)
    print(("[Sonoran Studio] %s"):format(message))
end

local function updaterError(message)
    print(("^1[Sonoran Studio Updater] ERROR: %s^7"):format(message))
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

local function validateInstallation()
    local valid = true
    if GetConvar(configuredConvar, "false") ~= "true" then
        updaterError("The required config was not executed. Remove any ensure/start line for sonoran_studio and add 'exec @sonoran_studio/sonoran_studio.cfg' to server.cfg.")
        valid = false
    end

    local normalizedPath = GetResourcePath(resourceName):gsub("\\", "/")
    if normalizedPath:match("/%[sonoran_studio%]/sonoran_studio$") == nil then
        updaterError("The resource must remain inside the downloaded [sonoran_studio] folder so updates are installed safely. Reinstall the ZIP directly into the resources folder.")
        valid = false
    end

    local helperState = GetResourceState(helperName)
    if helperState == "missing" or helperState == "unknown" then
        updaterError("The sonoran_studio_updatehelper resource is missing. Reinstall the complete standalone FiveM ZIP.")
        valid = false
    end
    return valid
end

local function startUpdate(latestVersion)
    updateInProgress = true
    log(("Downloading update %s..."):format(latestVersion))
    PerformHttpRequest(downloadUrl, function(status, data)
        if status ~= 200 or type(data) ~= "string" or data == "" then
            updateInProgress = false
            updaterError(("The update download failed with HTTP %s. The current version will continue running."):format(tostring(status)))
            return
        end

        local updatePath = GetResourcePath(resourceName) .. "/update.zip"
        local file, openError = io.open(updatePath, "wb")
        if file == nil then
            updateInProgress = false
            updaterError(("Could not write update.zip: %s. Check the resource folder's write permissions."):format(tostring(openError)))
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
        updaterError(("The update could not be installed: %s"):format(tostring(errorMessage or "unknown extraction error")))
        return
    end

    log("Automatic update installed. Restarting the resource...")
    SetConvar(helperSignal, "restart")
    Wait(1000)
    ExecuteCommand("ensure " .. helperName)
end)

local function checkForUpdate(manual)
    if not updaterReady then
        updaterError("The updater is unavailable because its installation checks did not pass. Review the earlier errors and run the bundled exec line.")
        return
    end
    if updateInProgress then
        log("An update is already in progress.")
        return
    end
    if manual then
        log("Checking for updates...")
    end

    PerformHttpRequest(versionUrl, function(status, data)
        if status ~= 200 then
            updaterError(("The update check failed with HTTP %s. The current version will continue running."):format(tostring(status)))
            return
        end

        local parsed, decoded = pcall(json.decode, data or "")
        local latestVersion = parsed and type(decoded) == "table" and decoded.resource or nil
        local currentVersion = GetResourceMetadata(resourceName, "version", 0)
        if type(latestVersion) ~= "string" or versionParts(latestVersion) == nil then
            updaterError("The update server returned an invalid version. The current version will continue running.")
            return
        end
        if not isNewerVersion(latestVersion, currentVersion) then
            if manual then
                log(("No update is available. Version %s is current."):format(tostring(currentVersion)))
            end
            return
        end
        startUpdate(latestVersion)
    end, "GET")
end

AddEventHandler("sonoranStudioUpdaterPermissionChecked", function(success, errorMessage)
    permissionCheckComplete = true
    if not success then
        updaterError(("Child-process permission is missing or the updater dependency could not start: %s. Use only 'exec @sonoran_studio/sonoran_studio.cfg' to start this resource."):format(tostring(errorMessage or "permission denied")))
        return
    end
    updaterReady = true
    log("Updater permissions verified.")
end)

RegisterCommand("sonoranstudio", function(source, args)
    if source ~= 0 then
        return
    end
    if tostring(args[1] or ""):lower() ~= "update" then
        log("Usage: sonoranstudio update")
        return
    end
    checkForUpdate(true)
end, false)

CreateThread(function()
    Wait(1000)
    if not validateInstallation() then
        return
    end
    exports[resourceName]:CheckUpdaterPermissions()

    while not permissionCheckComplete do
        Wait(100)
    end
    if not updaterReady then
        return
    end
    if GetConvar(autoUpdateConvar, "true") ~= "false" then
        log("Automatic updates are enabled.")
        checkForUpdate(false)
    else
        log("Automatic updates are disabled. Run 'sonoranstudio update' to check manually.")
    end

    while true do
        Wait(checkInterval)
        if GetConvar(autoUpdateConvar, "true") ~= "false" then
            checkForUpdate(false)
        end
    end
end)
