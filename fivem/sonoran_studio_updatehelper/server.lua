CreateThread(function()
    local helperName = GetCurrentResourceName()
    local signalName = "sonoran_studio_updatehelper_action"
    local action = GetConvar(signalName, "")

    if action ~= "restart" then
        print("[Sonoran Studio] The update helper is internal and must not be started manually. Use exec @sonoran_studio/sonoran_studio.cfg.")
        ExecuteCommand("stop " .. helperName)
        return
    end

    SetConvar(signalName, "")
    ExecuteCommand("refresh")
    Wait(1000)
    ExecuteCommand("restart sonoran_studio")
    Wait(1000)
    ExecuteCommand("stop " .. helperName)
end)
