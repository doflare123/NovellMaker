---@meta NovellMaker.library
-- Screen handlers/modules only. Not the full Lua standard library.
-- Finite numbers only; strings <=8192 UTF-16 units, tables <=1024 physical entries.
-- Shared per load/handler: 65536 work units / 32768 output characters, including imports.
---@param value any
---@return string
function type(value) end
---@param value nil|boolean|number|string No table/function/userdata conversion.
---@return string
function tostring(value) end
---@param value any Decimal invariant parsing, no base argument.
---@return number?
function tonumber(value) end
---@generic K,V
---@param value table<K,V> Snapshot iterator; no metatables.
---@return fun(): K?, V?
function pairs(value) end
---@generic V
---@param value V[] Dense 1..N, no metatables; snapshot iterator.
---@return fun(): integer?, V?
function ipairs(value) end

math = {}
---@type number
math.pi = 3.141592653589793
---@param x number
---@return number
function math.abs(x) end
---@param x number
---@return number
function math.ceil(x) end
---@param x number
---@return number
function math.floor(x) end
---@param x number
---@return number
function math.sqrt(x) end
---@param x number
---@return number
function math.sin(x) end
---@param x number
---@return number
function math.cos(x) end
---@param x number
---@return number
function math.tan(x) end
---@param x number
---@return number
function math.asin(x) end
---@param x number
---@return number
function math.acos(x) end
---@param x number
---@return number
function math.atan(x) end
---@param x number
---@return number
function math.exp(x) end
---@param x number
---@return number
function math.deg(x) end
---@param x number
---@return number
function math.rad(x) end
---@param y number
---@param x number
---@return number
function math.atan2(y, x) end
---@param x number
---@param y number
---@return number
function math.pow(x, y) end
---@param x number
---@param y number
---@return number
function math.fmod(x, y) end
---@param x number
---@param base? number
---@return number
function math.log(x, base) end
---@param x number
---@return number integral
---@return number fractional
function math.modf(x) end
---@param x number
---@param ... number At most 64 total arguments.
---@return number
function math.min(x, ...) end
---@param x number
---@param ... number At most 64 total arguments.
---@return number
function math.max(x, ...) end

string = {}
---@param text string
---@return integer UTF-16 length, not Unicode codepoints/UTF-8 bytes.
function string.len(text) end
---@param text string
---@param first integer 1-based; negative counts from end.
---@param last? integer Inclusive.
---@return string
function string.sub(text, first, last) end
---@param text string
---@return string Invariant case conversion.
function string.lower(text) end
---@param text string
---@return string Invariant case conversion.
function string.upper(text) end
---@param text string
---@return string Reverses UTF-16 code units.
function string.reverse(text) end
---@param text string
---@param count integer 0..8192, output <=8192 units.
---@param separator? string
---@return string
function string.rep(text, count, separator) end
---@param text string
---@param needle string Literal only; Lua patterns unsupported.
---@param first? integer
---@param plain? true Must be true if supplied.
---@return integer? first
---@return integer? last
function string.find(text, needle, first, plain) end

table = {}
---@overload fun(list: table, value: any)
---@param list table Dense list, nil forbidden.
---@param position integer
---@param value any
function table.insert(list, position, value) end
---@generic T
---@param list T[] Dense list.
---@param position? integer Default last element.
---@return T?
function table.remove(list, position) end
---@param list (string|number)[] Dense list.
---@param separator? string
---@param first? integer
---@param last? integer
---@return string
function table.concat(list, separator, first, last) end
