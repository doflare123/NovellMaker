using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using NovellMaker.Core.Execution;

namespace NovellMaker.Core.Projects;

internal static partial class ProjectJsonCodec
{
    private static readonly JsonSerializerOptions StrictSerializerOptions =
        CreateSerializerOptions(JsonUnmappedMemberHandling.Disallow);
    private static readonly JsonSerializerOptions CurrentSerializerOptions =
        CreateSerializerOptions(JsonUnmappedMemberHandling.Disallow, allowSlots: true);

    private static readonly JsonSerializerOptions VersionProbeSerializerOptions =
        CreateSerializerOptions(JsonUnmappedMemberHandling.Skip);
    private static readonly JsonSerializerOptions Version7SerializerOptions = CreateVersion7Options();
    private static readonly JsonSerializerOptions PreVersion10SerializerOptions = CreatePreVersion10Options();

    private static readonly JsonSerializerOptions Version11SerializerOptions = CreateVersion11Options();
    private static readonly JsonSerializerOptions Version12SerializerOptions = CreateLegacyScreenOptions();
    private static readonly JsonSerializerOptions Version15SerializerOptions = CreateSerializerOptions(JsonUnmappedMemberHandling.Disallow, true);
    private static JsonSerializerOptions CreateLegacyScreenOptions()
    {
        var options = CreateSerializerOptions(JsonUnmappedMemberHandling.Disallow);
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(RemoveScenePolicies);
        options.TypeInfoResolver = resolver;
        return options;
    }
    private static void RemoveScenePolicies(JsonTypeInfo info)
    {
        RemoveGeometryFields(info);
        RemoveLegacyBindingFields(info);
        if (info.Type == typeof(NovellMaker.Core.Screens.ScreenAsset))
            foreach (var property in info.Properties.Where(p => p.Name is "pauseOnOverlay" or "blockInputOnOverlay").ToArray()) info.Properties.Remove(property);
        if (info.Type == typeof(NovellMaker.Core.Screens.ScreenNode))
            foreach (var property in info.Properties) property.IsRequired = true;
    }
    private static void RemoveLegacyBindingFields(JsonTypeInfo info)
    {
        if (info.Type == typeof(NovellMaker.Core.Screens.ScreenButtonBinding))
            foreach (var property in info.Properties.Where(p => p.Name is "group" or "amount").ToArray()) info.Properties.Remove(property);
    }
    private static void RemoveGeometryFields(JsonTypeInfo info)
    {
        RemoveSlotFields(info);
        RemoveDialogueField(info);
        if (info.Type == typeof(NovellMaker.Core.Screens.ScreenNode))
            foreach (var property in info.Properties.Where(p => p.Name is "geometry" or "clipChildren" or "order" or "color").ToArray()) info.Properties.Remove(property);
    }
    private static void RemoveSlotFields(JsonTypeInfo info)
    {
        if (info.Type == typeof(NovellMaker.Core.Screens.ScreenButtonBinding))
            foreach (var property in info.Properties.Where(p => p.Name is "slotId" or "slotTitle").ToArray()) info.Properties.Remove(property);
    }
    private static void RemoveDialogueField(JsonTypeInfo info)
    {
        if (info.Type == typeof(NovellMaker.Core.Screens.ScreenNode))
            foreach (var property in info.Properties.Where(p => p.Name == "dialogue").ToArray()) info.Properties.Remove(property);
    }
    private static readonly JsonSerializerOptions Version14SerializerOptions = CreateVersion14Options();
    private static JsonSerializerOptions CreateVersion14Options()
    {
        var options = CreateSerializerOptions(JsonUnmappedMemberHandling.Disallow);
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            RemoveGeometryFields(info);
            if (info.Type == typeof(NovellMaker.Core.Screens.ScreenNode) || info.Type == typeof(NovellMaker.Core.Screens.ScreenButtonBinding))
                foreach (var property in info.Properties) property.IsRequired = true;
        });
        options.TypeInfoResolver = resolver; return options;
    }
    private static readonly JsonSerializerOptions Version13SerializerOptions = CreateVersion13Options();
    private static JsonSerializerOptions CreateVersion13Options()
    {
        var options = CreateSerializerOptions(JsonUnmappedMemberHandling.Disallow);
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            RemoveGeometryFields(info);
            if (info.Type == typeof(NovellMaker.Core.Screens.ScreenNode)) foreach (var property in info.Properties) property.IsRequired = true;
            RemoveLegacyBindingFields(info);
        });
        options.TypeInfoResolver = resolver; return options;
    }
    private static JsonSerializerOptions CreateVersion11Options()
    {
        var options = CreateSerializerOptions(JsonUnmappedMemberHandling.Disallow);
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            RemoveScenePolicies(info);
            if (info.Type == typeof(NovellMaker.Core.Screens.ScreenNode))
            {
                foreach (var property in info.Properties.Where(p => p.Name is "imageId" or "name").ToArray()) info.Properties.Remove(property);
                foreach (var property in info.Properties) property.IsRequired = true;
            }
        });
        options.TypeInfoResolver = resolver; return options;
    }

    private static JsonSerializerOptions CreatePreVersion10Options()
    {
        var options = CreateSerializerOptions(JsonUnmappedMemberHandling.Disallow);
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(ScenarioAction) && info.PolymorphismOptions is { } polymorphism)
                foreach (var type in polymorphism.DerivedTypes.Where(t => t.DerivedType == typeof(RunScriptAction)).ToArray()) polymorphism.DerivedTypes.Remove(type);
        });
        options.TypeInfoResolver = resolver;
        return options;
    }

    private static JsonSerializerOptions CreateVersion7Options()
    {
        var options = CreateSerializerOptions(JsonUnmappedMemberHandling.Disallow);
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(ScriptAction))
                foreach (var property in info.Properties.Where(p => p.Name == "references").ToArray()) info.Properties.Remove(property);
            if (info.Type == typeof(ScenarioAction) && info.PolymorphismOptions is { } polymorphism)
                foreach (var type in polymorphism.DerivedTypes.Where(t => t.DerivedType == typeof(LineEntryAction) || t.DerivedType == typeof(RunScriptAction)).ToArray()) polymorphism.DerivedTypes.Remove(type);
        });
        options.TypeInfoResolver = resolver;
        return options;
    }

    private static JsonSerializerOptions CreateSerializerOptions(
        JsonUnmappedMemberHandling unmappedMemberHandling, bool legacyDialogue = false, bool allowSlots = false)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (!allowSlots) RemoveSlotFields(info);
            if (legacyDialogue) RemoveDialogueField(info);
            if (info.Type == typeof(NovellMaker.Core.Screens.ScreenNode) || info.Type == typeof(NovellMaker.Core.Screens.ScreenButtonBinding) || info.Type == typeof(NovellMaker.Core.Screens.ScreenGeometry))
                foreach (var property in info.Properties) property.IsRequired = true;
        });
        return new()
        {
            TypeInfoResolver = resolver,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = unmappedMemberHandling,
            AllowDuplicateProperties = false,
            RespectRequiredConstructorParameters = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            WriteIndented = true,
        };
    }

    private static async Task<TDocument> DeserializeRequiredAsync<TDocument>(
        Stream stream,
        CancellationToken cancellationToken,
        JsonSerializerOptions? options = null)
        where TDocument : class
    {
        var document = await JsonSerializer.DeserializeAsync<TDocument>(
            stream,
            options ?? StrictSerializerOptions,
            cancellationToken).ConfigureAwait(false);

        return document ?? throw new ProjectFormatException("Файл проекта не содержит объекта проекта.");
    }

}
