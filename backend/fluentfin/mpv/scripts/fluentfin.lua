--[[ FluentFin MPV: FluentFin's key map in mpv's window, and the glue to uosc.

Bridge mode only (docs/bridge.md section 4a): the shim loads it with
whichever bar it loads (jellyfin_mpv_shim/fluentfin_bar.py). Every binding is
mp.add_key_binding's, never forced: a binding of the user's input.conf for the
same key wins.

With either bar:
  * the keys of FluentFin's own controls: k pause, j / l back / forward 10 s,
    Up / Down volume +-step held to 0..100, s skip the segment on offer,
    N next and P previous in the shim's queue (the last three are messages
    the shim acts on: fluentfin_osc.MESSAGES);
  * the Skip prompt: the bridge writes the offer's text ("Skip Intro") to
    user-data/fluentfin/skip, shown for a few seconds when it appears.

Once uosc runs (it publishes user-data/fluentfin/bar, its one local patch;
the fork does the first two of these itself):
  * the wheel is the volume, +-step held to 0..100, never a seek, and the
    horizontal wheel does nothing (uosc's own wheel zones, the volume slider
    and its menus, still take it where they are);
  * subtitles move above the bar while it is shown (sub-pos) and back when it
    hides, keeping a position the shim writes meanwhile;
  * uosc's managed buttons: fluentfin_prev, fluentfin_next (hidden at the end
    of the queue, user-data/fluentfin/next), fluentfin_skip (hidden with no
    offer);
  * "osc-visibility never" / "auto", the message the shim's enable_osc sends
    every OSC (the bridge's set_controls "ui" / "mpv"), takes uosc's elements
    away and gives them back.
]]

local options = {step = 5}
require("mp.options").read_options(options, "fluentfin")
local utils = require("mp.utils")

local ELEMENTS = "timeline,controls,volume,top_bar"

local uosc = false        -- uosc has published its bar
local bar = 0             -- the bottom bar's share of the height now, 0 hidden
local hidden = false      -- osc-visibility never: FluentFin draws the controls
local skip_text = ""      -- the segment on offer, "" none
local has_next = false

local function send(name) mp.commandv("script-message", name) end

local function to_uosc(...) mp.commandv("script-message-to", "uosc", ...) end

-- volume -----------------------------------------------------------------------

local function volume_by(notches)
    local volume = mp.get_property_number("volume")
    if not volume or notches == 0 then return end
    local target
    if notches > 0 then
        if volume >= 100 then return end
        target = math.min(100, volume + options.step * notches)
    else
        if volume <= 0 then return end
        target = math.max(0, volume + options.step * notches)
    end
    if uosc then
        mp.commandv("no-osd", "set", "volume", tostring(target))
        to_uosc("flash-elements", "volume")
    else
        mp.commandv("osd-auto", "set", "volume", tostring(target))
    end
end

-- keys ---------------------------------------------------------------------------

mp.add_key_binding("k", "pause", function() mp.commandv("cycle", "pause") end)
mp.add_key_binding("j", "back", function() mp.commandv("seek", "-10") end)
mp.add_key_binding("l", "forward", function() mp.commandv("seek", "10") end)
mp.add_key_binding("UP", "volume-up", function() volume_by(1) end,
                   {repeatable = true})
mp.add_key_binding("DOWN", "volume-down", function() volume_by(-1) end,
                   {repeatable = true})
mp.add_key_binding("s", "skip", function() send("fluentfin-skip") end)
mp.add_key_binding("N", "next", function() send("fluentfin-next") end)
mp.add_key_binding("P", "previous", function() send("fluentfin-prev") end)

-- uosc's buttons -----------------------------------------------------------------

local function button(name, data) to_uosc("set-button", name, utils.format_json(data)) end

local function update_buttons()
    if not uosc then return end
    button("fluentfin_prev", {icon = "skip_previous", tooltip = "Previous",
                              command = "script-message fluentfin-prev"})
    button("fluentfin_next", {icon = "skip_next", tooltip = "Next",
                              command = "script-message fluentfin-next",
                              hide = not has_next})
    button("fluentfin_skip", {icon = "fast_forward",
                              tooltip = skip_text ~= "" and skip_text or "Skip",
                              command = "script-message fluentfin-skip",
                              hide = skip_text == ""})
end

-- subtitles above the bar ---------------------------------------------------------
-- `base` is the sub-pos to put back: what it was when we raised it, or what
-- somebody else wrote since. `raised` is the value we wrote.

local raise = {base = nil, raised = nil}
local restore_timer = nil

-- Put the subtitles back after RESTORE_DELAY, not at once: the bridge's
-- set_controls "ui" hides the bar and writes its own sub-pos in two commands
-- back to back, and a restore made between our read of sub-pos and its write
-- landing put the old base back over the bridge's value. By then its write has
-- reached the sub-pos observer below, which makes it the base.
local RESTORE_DELAY = 0.15

local function shown() return uosc and not hidden and bar > 0 end

local function restore()
    restore_timer = nil
    if shown() or not raise.base then return end
    local base, raised = raise.base, raise.raised
    raise.base, raise.raised = nil, nil
    -- A value somebody else wrote that the observer has not seen yet is
    -- newer than `base`: keep it.
    local now = mp.get_property_number("sub-pos")
    if raised and now and math.abs(now - raised) > 0.01 then return end
    mp.set_property_number("sub-pos", base)
end

local function update_subs()
    if shown() then
        if restore_timer then
            restore_timer:kill()
            restore_timer = nil
        end
        local base = raise.base or mp.get_property_number("sub-pos", 100)
        local target = math.min(base, math.floor(100 - bar * 100))
        raise.base = base
        if raise.raised ~= target then
            raise.raised = target
            mp.set_property_number("sub-pos", target)
        end
    elseif raise.base and not restore_timer then
        restore_timer = mp.add_timeout(RESTORE_DELAY, restore)
    end
end

-- The shim writes sub-pos too (its subtitle position, at every start): a
-- value that is not ours, arriving while raised, is the one to put back.
mp.observe_property("sub-pos", "number", function(_, value)
    if raise.base and raise.raised and value
        and math.abs(value - raise.raised) > 0.01 then
        raise.base, raise.raised = value, nil
        update_subs()
    end
end)

-- the wheel ------------------------------------------------------------------------

local function wheel(direction)
    -- A precise touchpad reports fractions of a notch (`scale`): they add up
    -- to whole steps instead of each moving a whole step.
    local pending = 0
    return function(e)
        if e.event == "up" then return end
        pending = pending + direction * (tonumber(e.scale) or 1)
        local whole = pending >= 0 and math.floor(pending) or math.ceil(pending)
        pending = pending - whole
        volume_by(whole)
    end
end

local function nothing() end

-- uosc up --------------------------------------------------------------------------

local function on_uosc()
    mp.add_key_binding("WHEEL_UP", "wheel-up", wheel(1), {complex = true})
    mp.add_key_binding("WHEEL_DOWN", "wheel-down", wheel(-1), {complex = true})
    mp.add_key_binding("WHEEL_LEFT", "wheel-left", nothing)
    mp.add_key_binding("WHEEL_RIGHT", "wheel-right", nothing)
    if hidden then to_uosc("disable-elements", mp.get_script_name(), ELEMENTS) end
    update_buttons()
end

mp.observe_property("user-data/fluentfin/bar", "native", function(_, value)
    if value == nil then return end
    if not uosc then
        uosc = true
        on_uosc()
    end
    bar = tonumber(value) or 0
    update_subs()
end)

-- uosc says so when it starts: buttons sent before it listened were lost.
mp.register_script_message("uosc-version", function() update_buttons() end)

-- what the bridge says --------------------------------------------------------------

-- "native", never "string": a user-data string observed as "string" arrives
-- quoted, as JSON ("\"Skip Intro\"").
mp.observe_property("user-data/fluentfin/skip", "native", function(_, value)
    value = type(value) == "string" and value or ""
    if value == skip_text then return end
    skip_text = value
    if value ~= "" and not hidden then
        mp.osd_message(value .. "  (S)", 4)
        if uosc then to_uosc("flash-elements", "timeline,controls") end
    end
    update_buttons()
end)

mp.observe_property("user-data/fluentfin/next", "native", function(_, value)
    local now = value == "1"
    if now == has_next then return end
    has_next = now
    update_buttons()
end)

mp.register_script_message("osc-visibility", function(mode)
    local now = mode == "never"
    if now == hidden then return end
    hidden = now
    if uosc then
        to_uosc("disable-elements", mp.get_script_name(), hidden and ELEMENTS or "")
    end
    update_subs()
end)
