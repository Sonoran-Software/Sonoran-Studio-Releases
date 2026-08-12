fx_version 'cerulean'
game 'gta5'

author 'Sonoran Software'
description 'Standalone FiveM integration for Sonoran Studio'
version '1.0.1'

client_script 'client.lua'

server_scripts {
    'server/unzip.js',
    'server/updater.lua'
}

ui_page 'nui/index.html'

files {
    'nui/index.html',
    'nui/bridge.js'
}
