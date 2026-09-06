-- FilmRefit - DaVinci Resolve proxy linker
-- Targeted at DaVinci Resolve Studio 21.x.
--
-- Install this file in Resolve's Fusion/Scripts/Utility folder, restart Resolve,
-- then run it from Workspace > Scripts > FilmRefit Proxy Linker.
--
-- The script is intentionally self-contained: no external Lua modules are needed.

local PROXY_SUFFIX = "_PROXY"
local MEZZANINE_SUFFIX = "_MEZZANINE"
local PROXY_EXTENSIONS = { "mov", "MOV", "mp4", "MP4", "mxf", "MXF" }

-- ---------------------------------------------------------------------------
-- Resolve helpers
-- ---------------------------------------------------------------------------

local function get_fusion()
    if fusion ~= nil then return fusion end
    if fu ~= nil then return fu end
    if type(Fusion) == "function" then
        local ok, result = pcall(Fusion)
        if ok then return result end
    end
    return nil
end

local function get_resolve()
    if type(Resolve) == "function" then
        local ok, result = pcall(Resolve)
        if ok and result then return result end
    end
    local f = get_fusion()
    if f and f.GetResolve then
        local ok, result = pcall(function() return f:GetResolve() end)
        if ok and result then return result end
    end
    if app and app.GetResolve then
        local ok, result = pcall(function() return app:GetResolve() end)
        if ok and result then return result end
    end
    return nil
end

local function show_message(title, body)
    print(title)
    print(body)
    local f = get_fusion()
    if f and f.GetCurrentComp then
        local ok_comp, comp = pcall(function() return f:GetCurrentComp() end)
        if ok_comp and comp and comp.AskUser then
            pcall(function()
                comp:AskUser(title, {
                    { "Message", "Text", Default = body, ReadOnly = true, Lines = 14, Wrap = true }
                })
            end)
        end
    end
end

-- ---------------------------------------------------------------------------
-- Path helpers
-- ---------------------------------------------------------------------------

local function file_exists(path)
    local f = io.open(path, "rb")
    if f then
        f:close()
        return true
    end
    return false
end

local function split_path(path)
    path = tostring(path or "")
    local dir, name = path:match("^(.*[\\/])([^\\/]*)$")
    if dir and name then
        return dir, name
    end
    return "", path
end

local function strip_extension(file_name)
    return (file_name:gsub("%.[^%.]*$", ""))
end

local function ends_with_ignore_case(value, suffix)
    return string.lower(value):sub(-#suffix) == string.lower(suffix)
end

local function get_source_path(item)
    local props = {}
    pcall(function() props = item:GetClipProperty() or {} end)

    local keys = { "File Path", "FilePath", "File Path 1", "Filename", "File Name" }
    for _, key in ipairs(keys) do
        local value = props[key]
        if value and tostring(value) ~= "" then
            return tostring(value)
        end
    end

    return nil
end

local function find_proxy_path(source_path)
    local dir, file_name = split_path(source_path)
    local stem = strip_extension(file_name)
    if stem == "" then
        return nil
    end

    for _, extension in ipairs(PROXY_EXTENSIONS) do
        local candidate = dir .. stem .. PROXY_SUFFIX .. "." .. extension
        if file_exists(candidate) then
            return candidate
        end
    end

    return nil
end

-- ---------------------------------------------------------------------------
-- Media pool traversal
-- ---------------------------------------------------------------------------

local function folder_name(folder)
    local name = "(unnamed bin)"
    pcall(function() name = tostring(folder:GetName()) end)
    return name
end

local function walk_folder(folder, visitor)
    local clips = {}
    pcall(function() clips = folder:GetClipList() or {} end)
    for _, clip in pairs(clips) do
        visitor(clip, folder)
    end

    local folders = {}
    pcall(function() folders = folder:GetSubFolderList() or {} end)
    for _, child in pairs(folders) do
        walk_folder(child, visitor)
    end
end

local function sorted_keys(set)
    local keys = {}
    for key, _ in pairs(set) do
        keys[#keys + 1] = key
    end
    table.sort(keys)
    return keys
end

-- ---------------------------------------------------------------------------
-- Link proxies
-- ---------------------------------------------------------------------------

local function main()
    local resolve = get_resolve()
    if not resolve then error("Could not access the DaVinci Resolve scripting object") end

    local project_manager = resolve:GetProjectManager()
    local project = project_manager and project_manager:GetCurrentProject() or nil
    if not project then error("No current Resolve project is open") end

    local media_pool = project:GetMediaPool()
    local root = media_pool and media_pool:GetRootFolder() or nil
    if not root then error("Could not access the current project's media pool") end

    local scanned = 0
    local linked = 0
    local missing = 0
    local failed = 0
    local no_path = 0
    local skipped_outputs = 0
    local failed_items = {}
    local missing_items = {}

    walk_folder(root, function(item, folder)
        scanned = scanned + 1
        local source_path = get_source_path(item)
        if not source_path then
            no_path = no_path + 1
            return
        end

        local _, file_name = split_path(source_path)
        local stem = strip_extension(file_name)
        if ends_with_ignore_case(stem, PROXY_SUFFIX) or ends_with_ignore_case(stem, MEZZANINE_SUFFIX) then
            skipped_outputs = skipped_outputs + 1
            return
        end

        local proxy_path = find_proxy_path(source_path)
        if not proxy_path then
            missing = missing + 1
            missing_items[file_name] = true
            return
        end

        local ok, result = pcall(function() return item:LinkProxyMedia(proxy_path) end)
        if ok and result then
            linked = linked + 1
        else
            failed = failed + 1
            failed_items[string.format("%s (%s)", file_name, folder_name(folder))] = true
        end
    end)

    local project_name = "(unnamed project)"
    pcall(function() project_name = tostring(project:GetName()) end)
    local summary = string.format(
        "Project: %s\n\nMedia pool clips scanned: %d\nLinked proxies: %d\nMissing proxies: %d\nSkipped FilmRefit outputs: %d\nClips without file paths: %d\nResolve rejected links: %d",
        project_name,
        scanned,
        linked,
        missing,
        skipped_outputs,
        no_path,
        failed
    )

    local failed_list = sorted_keys(failed_items)
    if #failed_list > 0 then
        summary = summary .. "\n\nFailed links:\n" .. table.concat(failed_list, "\n")
    end

    local missing_list = sorted_keys(missing_items)
    if #missing_list > 0 and #missing_list <= 30 then
        summary = summary .. "\n\nMissing proxies:\n" .. table.concat(missing_list, "\n")
    elseif #missing_list > 30 then
        summary = summary .. "\n\nMissing proxies: more than 30 clips; check Resolve's console for details."
        for _, name in ipairs(missing_list) do
            print("Missing FilmRefit proxy: " .. name)
        end
    end

    show_message("FilmRefit Proxy Link Complete", summary)
end

local ok, err = xpcall(main, debug.traceback)
if not ok then
    show_message("FilmRefit Proxy Link Error", tostring(err))
end
