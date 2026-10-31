"""Check the repository-local Lua reference against the actual VM registrations.

No third-party packages or production API changes. Run from any directory with Python 3.
The explicitly interpreted registration patterns make API changes fail loudly.
"""
from pathlib import Path
import json
import re
import sys
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[1]
DOC = ROOT / "docs/lua-reference"


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def named_calls(text, calls):
    pattern = r'\b(?:' + '|'.join(calls) + r')\("([a-z0-9_]+)"'
    return set(re.findall(pattern, text))


def inventory():
    game = named_calls(read("src/NovellMaker.Core/Execution/LuaActionScript.cs"), ["Bind"])
    worker = read("src/NovellMaker.Core/Screens/Lua/ScreenLuaWorker.cs")
    ui = named_calls(worker, ["Bind", "Input"])
    ui.update(re.findall(r'ui\.Set\("([a-z_]+)", DynValue.NewCallback', worker))
    for path in ["src/NovellMaker.Core/Scripting/LuaRandomApi.cs",
                 "src/NovellMaker.Core/Screens/Lua/LuaAudioApi.cs",
                 "src/NovellMaker.Core/Screens/Lua/LuaAnimationApi.cs"]:
        code = read(path)
        ui.update(named_calls(code, ["Bind"]))
        ui.update(re.findall(r'ui\.Set\("([a-z_]+)", DynValue.NewCallback', code))
    loader = read("src/NovellMaker.Core/Scripting/LuaModuleLoader.cs")
    ui.update(re.findall(r'api\.ui\.([a-z_]+)=function\(', loader))
    std = read("src/NovellMaker.Core/Scripting/LuaStandardLibrary.cs")
    library = set(re.findall(r'Bind\(environment, "([a-z_]+)"', std))
    library.update("math." + n for n in named_calls(std, ["Unary", "Binary"]))
    for table, prefix in [("math", "math"), ("strings", "string"), ("tables", "table")]:
        library.update(prefix + "." + n for n in re.findall(r'Bind\(' + table + r', "([a-z_]+)"', std))
    # Name-bearing loops are source, not a manually maintained API manifest.
    for names, target in re.findall(r'foreach \(var name in new\[\] \{([^}]+)\}\)\s*Bind\((math|strings), name', std):
        prefix = "math" if target == "math" else "string"
        library.update(prefix + "." + n for n in re.findall(r'"([a-z_]+)"', names))
    return {"game": {"game." + n for n in game},
            "ui": {"ui." + n for n in ui}, "standard": library}


def main():
    errors = []
    groups = inventory()
    actual = set().union(*groups.values())
    index = (DOC / "index.md").read_text(encoding="utf-8")
    function_index = index.split("## Полный алфавитный указатель", 1)[1]
    indexed = re.findall(r'^- \[([a-z_][a-z0-9_]*(?:\.[a-z0-9_]+)?)\]\(([^)]+)\)', function_index, re.M)
    names = [n for n, _ in indexed]
    if len(names) != len(set(names)):
        errors.append("Duplicate functions in the index")
    for name in sorted(actual - set(names)):
        errors.append("Registered function missing from index: " + name)
    for name in sorted(set(names) - actual):
        errors.append("Unregistered function in index: " + name)
    descriptions = {}
    required = ["**Доступность:**", "**Аргументы и ошибки:**", "**Возврат:**",
                "**Эффект и ограничения:**", "**Пример**", "**Связи:**"]
    for path in DOC.glob("*.md"):
        text = path.read_text(encoding="utf-8")
        for match in re.finditer(r'^## ([a-z_][a-z0-9_]*(?:\.[a-z0-9_]+)?)\s*\n', text, re.M):
            name = match[1]
            if name not in actual:
                continue
            start = match.end()
            end = re.search(r'^## ', text[start:], re.M)
            block = text[start:start + end.start()] if end else text[start:]
            if name in descriptions:
                errors.append("Duplicate function description: " + name)
            descriptions[name] = path.name
            for field in required:
                if field not in block:
                    errors.append(f"{name}: missing {field}")
            if "```lua" not in block or "../../src/" not in block or "../../tests/" not in block:
                errors.append(f"{name}: missing signature/example/source/test")
    for name in sorted(actual - descriptions.keys()):
        errors.append("Registered function without its own description: " + name)

    protocol = read("src/NovellMaker.Core/Screens/Lua/ScreenLuaProtocol.cs")
    event_validator = protocol.split("internal static void ValidateEvent(", 1)[1].split("internal static ScreenPreparedBatch Prepare(", 1)[0]
    ordinary_events = re.search(r'value.Kind is not \("click"([\s\S]*?)\)\s*\|\|', event_validator)
    if ordinary_events is None:
        errors.append("Event registration pattern changed: review the extractor")
        events = set()
    else:
        events = {"click"} | set(re.findall(r'"([a-z_]+)"', ordinary_events[1]))
        events.update(re.findall(r'value.Kind == "([a-z_]+)"', event_validator))
    event_docs = (DOC / "events.md").read_text(encoding="utf-8")
    for event in sorted(events):
        if not re.search(r'^(?:## ' + re.escape(event) + r'\s*$|\| ' + re.escape(event) + r' \|)', event_docs, re.M):
            errors.append("Public event without description: " + event)

    # LuaCATS is a secondary source: compare names, never infer VM APIs from it.
    annotations = set()
    for file in ["game.lua", "ui.lua", "library.lua"]:
        annotations.update(re.findall(r'^function ([a-z0-9_.]+)\(', read("src/NovellMaker.Editor/LuaApi/" + file), re.M))
    cats_missing = sorted(actual - annotations)
    cats_obsolete = sorted(annotations - actual)
    if cats_missing or cats_obsolete:
        errors.append(f"LuaCATS name mismatch: missing={cats_missing}, obsolete={cats_obsolete}")

    link_count = 0
    for path in list(DOC.glob("*.md")) + [ROOT / "README.md"]:
        text = path.read_text(encoding="utf-8")
        for label, raw in re.findall(r'\[([^\]]*)\]\(([^)]+)\)', text):
            if re.match(r'^[a-z]+://', raw) or raw.startswith("mailto:"):
                continue
            raw = unquote(raw.strip("<>"))
            file, _, fragment = raw.partition("#")
            target = (path.parent / file).resolve() if file else path
            link_count += 1
            if not target.is_file():
                errors.append(f"Broken link in {path.name}: {raw}")
                continue
            if fragment and target.suffix == ".md":
                body = target.read_text(encoding="utf-8")
                explicit = set(re.findall(r'<a id="([^"]+)"', body))
                headings = set()
                for h in re.findall(r'^#{1,6} (.+)$', body, re.M):
                    slug = re.sub(r'[^\w\s-]', '', h.replace('`', '').lower()).replace(' ', '-')
                    headings.add(slug)
                if fragment not in explicit | headings:
                    errors.append(f"Broken anchor in {path.name}: {raw}")
    result = {"functions": {k: len(v) for k, v in groups.items()}, "total": len(actual),
              "descriptions": len(descriptions), "events": len(events), "links_checked": link_count,
              "luacats_missing": cats_missing, "luacats_obsolete": cats_obsolete,
              "errors": errors}
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
