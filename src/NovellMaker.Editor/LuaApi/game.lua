---@meta NovellMaker.game
-- Scenario API v1. Fresh VM per call, queue computed completely before execution.
-- ui.*, modules and the screen standard library are unavailable in scenario scripts.
---@class NMPlacement
---@field visible boolean
---@field state? string
---@field x? number
---@field y? number
---@field scale? number
---@field layer? integer
---@class NMChoice
---@field id string GUID.
---@field text string
---@field scene string Target scene GUID.
---@field action string Target low-level action GUID.
game = {}
---@param key string
---@return string?
function game.get(key) end
---@param key string
---@param value string
function game.set(key, value) end
---@return number Engine time in seconds.
function game.time() end
---@param object string GUID or refs alias.
---@return boolean
function game.visible(object) end
---@param object string
---@return NMPlacement
function game.object(object) end
---@param object string
function game.show(object) end
---@param object string
function game.hide(object) end
---@param object string
---@param state string
function game.state(object, state) end
---@param line string GUID.
function game.text(line) end
---@param object string
---@param x number
---@param y number
---@param scale number
---@param layer integer -1000..1000.
function game.place(object, x, y, scale, layer) end
---@param object string
function game.auto(object) end
---@param object string
---@param x number
---@param y number
---@param scale number
---@param layer integer
---@param seconds number
function game.move(object, x, y, scale, layer, seconds) end
---@param seconds number Pauses the queue, not Lua computation.
function game.wait(seconds) end
function game.wait_input() end
function game.wait_actions() end
---@param token? string Interaction GUID.
function game.wait_click(token) end
---@param id string
---@param text string
---@param x number
---@param y number
---@param width number
---@param height number
function game.hotspot(id, text, x, y, width, height) end
---@param result string Writes minigame.result; no mini-game lifecycle.
function game.finish(result) end
---@param resource string Registered script GUID or refs.script. Queued, no return value.
function game.run(resource) end
---@param scene string
---@param action string Low-level GUID, prefer jump_line for author phrases.
function game.jump(scene, action) end
---@param scene string
---@param line string Phrase GUID.
function game.jump_line(scene, line) end
---@param options NMChoice[] 1..8.
function game.choice(options) end
