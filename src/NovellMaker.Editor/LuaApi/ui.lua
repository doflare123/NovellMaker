---@meta NovellMaker.ui
-- NovellMaker screen API, wire 14 / Lua 5.2. Tooling only; never execute this file.
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
---@class NMEvent
---@field kind 'click'|'move'|'hover'|'update'|'trigger'|'service_result'
---@field node? NMHandle
---@field x number Logical viewport coordinates.
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
---@field on_load? fun(event: table) Empty event table at opening.
---@field on_event fun(event: NMEvent) Must finish within the common instruction/work budget.

---@type table<string,NMHandle> GUI aliases of this canvas; modules receive handles as arguments.
refs = {}
---@type table<string,string> Registered Lua resource aliases belonging to the current file.
modules = {}

ui = {}
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
