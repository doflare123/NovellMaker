---@meta NovellMaker.ui
-- NovellMaker screen API, wire 18 / Lua 5.2. Tooling only; never execute this file.
-- Calls enqueue one atomic batch. A handler must return; waits do not suspend Lua.

---@alias NMHandle string Opaque resource/instance/node identity, valid only in its live instance.
---@alias NMValue nil|boolean|number|string|NMNull|NMValue[]|table<string,NMValue>
---@class NMNull
---@class NMGeometry
---@field x? number
---@field y? number
---@field width? number Positive, at most 100000.
---@field height? number Positive, at most 100000.
---@field anchor_x? number
---@field anchor_y? number
---@field pivot_x? number
---@field pivot_y? number
---@field scale_x? number
---@field scale_y? number
---@field rotation? number Degrees.
---@field opacity? number 0..1.
---@field color? string '#RRGGBB'; alpha is controlled by opacity.
---@field order? integer -100000..100000.
---@field clip_children? boolean
---@class NMSettings
---@field master_volume number
---@field master_muted boolean
---@field sound_volume number
---@field sound_muted boolean
---@field effect_volume number
---@field effect_muted boolean
---@field voice_volume number
---@field voice_muted boolean
---@field fullscreen boolean
---@class NMSlot
---@field id string
---@field status string
---@field compatible boolean
---@field legacy boolean
---@field diagnostic? string
---@field title? string
---@field scene? string
---@field phrase? string
---@field revision? string
---@field saved_at? string UTC ISO 8601.
---@field game_time? number
---@class NMRelease
---@field current_version string Canonical four-part version.
---@field latest_version string
---@field update_available boolean
---@field page_url string Validated GitHub HTTPS page; data only.
---@field checked_at string UTC ISO 8601.
---@field from_cache boolean
---@class NMEvent
---@field provider? string github_releases_v1 for CheckGameRelease.
---@field code? string Ok/Disabled/Busy/RateLimited/Cancelled/Timeout or provider error.
---@field retry_after_seconds? integer
---@field release? NMRelease
---@field kind 'click'|'move'|'hover'|'update'|'trigger'|'service_result'|'animation_result'|'audio_result'|'pointer_down'|'pointer_up'|'pointer_cancel'|'drag_start'|'drag'|'drag_end'|'hover_enter'|'hover_leave'|'focus'|'blur'|'key_down'|'key_up'|'input_reset'
---@field audio_id? string ID returned by ui.play_audio.
---@field animation_id? string ID returned by ui.animate.
---@field status? 'completed'|'cancelled'|'stopped'|'failed'
---@field reason? string finished/requested/replaced/property_set/removed/reparented/invalid_geometry.
---@field node? NMHandle
---@field shift? boolean Keyboard modifiers, physical key events only.
---@field control? boolean
---@field alt? boolean
---@field x number Logical viewport coordinates, including outside the viewport while captured.
---@field y number
---@field seconds number Engine delta, 0..1. No OS clock.
---@field trigger_id? string
---@field trigger_data? string
---@field request_id? string
---@field operation? string C# service operation name, e.g. ListSlots.
---@field success? boolean
---@field message? string
---@field slot_id? string
---@field slots? NMSlot[]
---@field settings? NMSettings
---@field key? string
---@field value? string
---@field persisted? boolean Whether the whole current profile was saved.
---@class NMHandlers
---@field input_events? boolean Opt in to physical input events. Default false preserves existing click-only handlers.
---@field on_load? fun(event: table) Empty event table at opening.
---@field on_event fun(event: NMEvent) Must finish within the common instruction/work budget.

---@type table<string,NMHandle> GUI aliases of this canvas; modules receive handles as arguments.
refs = {}
---@type table<string,string> Registered Lua resource aliases belonging to the current file.
modules = {}
---@type table<string,string> Registered WAV aliases of the root handler; modules receive them as arguments.
audio = {}

ui = {}
---@param node NMHandle Enabled visible Button/Area in this live instance.
-- Only the current pointer sequence can be captured; late replies never recover a released press.
function ui.capture_pointer(node) end
-- Cancels this instance's current press/drag. No synthetic click.
function ui.release_pointer() end
---@param node NMHandle Enabled visible Button/Area. Focus/blur delivered asynchronously.
function ui.focus(node) end
---@param asset string Registered audio.<alias>, never a file path.
---@param group 'sound'|'effect'|'voice'
---@param loop boolean
---@param volume number Source gain 0..1, multiplied by group and Master.
---@return string id Owned source ID; audio_result reports completed/stopped/failed once.
-- Existing playback continues while an ordinary update awaits Lua; input/results and explicit pause still hold it.
function ui.play_audio(asset, group, loop, volume) end
---@param id string Live source belonging to this handler instance.
function ui.stop_audio(id) end
---@param id string Live source belonging to this handler instance.
---@param volume number Source gain 0..1; no profile write.
function ui.set_audio_volume(id, volume) end
---@type NMNull Explicit null in lists/dictionaries; immutable, no CLR reflection.
ui.null = nil
---@param node NMHandle
---@param text string
function ui.set_text(node, text) end
---@param node NMHandle
---@param visible boolean
function ui.set_visible(node, visible) end
---@param node NMHandle
---@param enabled boolean
function ui.set_enabled(node, enabled) end
---@param node NMHandle
---@param patch NMGeometry
function ui.set_geometry(node, patch) end
---@alias NMAnimation string Opaque description, valid only in the current handler.
---@alias NMEasing 'linear'|'in_quad'|'out_quad'|'in_out_quad'|'in_cubic'|'out_cubic'|'in_out_cubic'
---@class NMTweenProperties
---@field x? number
---@field y? number
---@field width? number
---@field height? number
---@field anchor_x? number
---@field anchor_y? number
---@field pivot_x? number
---@field pivot_y? number
---@field scale_x? number
---@field scale_y? number
---@field rotation? number
---@field opacity? number
---@param node NMHandle Own live component.
---@param properties NMTweenProperties Numeric target values.
---@param seconds number Positive, at most 3600.
---@param easing? NMEasing Defaults to linear.
---@return NMAnimation
function ui.tween(node, properties, seconds, easing) end
---@param children NMAnimation[] Nonempty dense list.
---@return NMAnimation
function ui.sequence(children) end
---@param children NMAnimation[] Overlapping writes to a property are rejected.
---@return NMAnimation
function ui.parallel(children) end
---@param description NMAnimation
---@return string animation_id Completion arrives as animation_result.
function ui.animate(description) end
---@param animation_id string Own running group; absent/already finished is a no-op.
function ui.cancel_animation(animation_id) end
---@param node NMHandle
---@param parent NMHandle Own live node; cycles/depth rejected.
function ui.set_parent(node, parent) end
---@param node NMHandle Removes subtree; removed handles cannot be reused.
function ui.remove(node) end
---@param alias string Own canvas alias.
---@return NMHandle? node
function ui.find(alias) end
---@param template NMHandle Own live subtree.
---@param parent NMHandle
---@return NMHandle root
---@return table<NMHandle,NMHandle> mapping Old handles to copies.
function ui.create(template, parent) end
function ui.close() end
---@param key string Reads captured story variables, not component state.
---@return string?
function ui.get(key) end
---@return NMValue Copy of host-committed component data.
function ui.get_state() end
---@param value NMValue Explicit data only; no cycles/functions/metatables. Handler only.
function ui.set_state(value) end
---@generic T: table
---@param value T
---@return T Marks an empty table as a list.
function ui.list(value) end
---@generic T: table
---@param value T
---@return T Marks a table as an object.
function ui.object(value) end
---@param resource string Use modules.alias, not a path. Cached per instance; module must return table.
---@return table exports
function ui.import(resource) end
---@overload fun(upper: integer): integer
---@overload fun(lower: integer, upper: integer): integer
---@return number value In [0,1); integer bounds inclusive, int32. Handler only, 1024 raw draws.
function ui.random() end
---@param seed integer 0..4294967295. Costs two RNG draws. Handler only.
function ui.seed_random(seed) end
---@return string snapshot 'pcg32-v1:' plus 16 lowercase hex; no Lua number precision loss.
function ui.get_random_state() end
---@param snapshot string Restore explicit RNG data, not a Lua stack. Handler only.
function ui.set_random_state(snapshot) end
---@param level 'info'|'warning'|'error'
---@param message string At most 1024 UTF-16 units; 32 entries/8192 units per call; excess counted/dropped.
function ui.log(level, message) end
---@param id string Author trigger key.
---@param data? string Enqueued after commit, delivered as a later on_event.
function ui.trigger(id, data) end

-- At most one service command per batch; no ui.close/trigger in the same batch.
-- request_id is immediate; result arrives in a later on_event, not a synchronous return.
---@param scene? string GUID; optional story start.
---@return string request_id
function ui.new_game(scene) end
---@return string request_id
function ui.main_menu() end
---@return string request_id
function ui.end_session() end
---@return string request_id
function ui.continue_game() end
---@return string request_id
function ui.list_slots() end
---@param id? string nil creates a new slot.
---@param title? string
---@return string request_id
function ui.save_slot(id, title) end
---@param id string
---@return string request_id
function ui.load_slot(id) end
---@param id string
---@return string request_id
function ui.delete_slot(id) end
---@return string request_id
function ui.get_settings() end
---Exactly zero arguments. No network until the whole batch commits. No automatic retry.
---@return string request_id Result arrives as service_result, operation CheckGameRelease.
function ui.check_game_release() end
---Exactly one own request ID. Completed request is a no-op; foreign ID rejects the batch.
---@param request_id string
function ui.cancel_release_check(request_id) end
---@param key 'master_volume'|'master_muted'|'sound_volume'|'sound_muted'|'effect_volume'|'effect_muted'|'voice_volume'|'voice_muted'|'fullscreen'
---@param value number|boolean Volume 0..1, flags boolean.
---@return string request_id
function ui.set_setting(key, value) end
---@param key string
---@return string request_id
function ui.get_persistent(key) end
---@param key string
---@param value? string nil removes; empty string is a value.
---@return string request_id
function ui.set_persistent(key, value) end

---Parameters captured by the scenario launch; only available inside a mini-game handler.
---@return NMValue
function ui.minigame_parameters() end
---True for a restored instance. on_load must only rebuild visuals from saved data.
---@return boolean
function ui.is_restoring() end
---Complete this mini-game once with schema-checked structured data. Last command in the batch.
---@param result NMValue
function ui.complete_minigame(result) end
---Cancel this mini-game and continue the scenario with status cancelled.
function ui.cancel_minigame() end
