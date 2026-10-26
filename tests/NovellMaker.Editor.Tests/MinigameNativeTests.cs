using System.Diagnostics;
using System.Text.Json;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scripting;
using NovellMaker.Editor.Examples;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Export;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MinigameNativeTests
{
    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task CardPuzzleSavesMidSelectionRestartsRestoresSameLayoutAndRngAndBranchesOnce(bool packaged)
    {
        if(Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE")!="1"||packaged&&Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE")!="1")
            Assert.Inconclusive("Enable NativeRuntime and WindowsExport smoke.");
        var repo=new DirectoryInfo(AppContext.BaseDirectory);while(repo is not null&&!File.Exists(Path.Combine(repo.FullName,"NovellMaker.slnx")))repo=repo.Parent;
        var directory=Path.Combine(repo!.FullName,"artifacts","screenshots","stage8-minigame",packaged?"export":"preview",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var project=ExampleMinigameModule.AddTo(NovelProject.Create("Сохраняемый карточный пазл"));var story=project.Scenes[0];var puzzle=project.Scenes.Last();
        var file=Path.Combine(directory,"puzzle.novell");await new JsonProjectStore().SaveAsync(file,project);
        var runtime=Path.Combine(repo.FullName,"src","NovellMaker.Runtime","bin","Release","net10.0","NovellMaker.Runtime.exe");
        if(packaged)
        {
            var output=Path.Combine(directory,"game");await new WindowsExportService(Path.Combine(repo.FullName,"artifacts","export-templates","win-x64"),new ImageAssetService()).ExportAsync(project,directory,story.Id,output);
            runtime=Path.Combine(output,"NovellMaker.Runtime.exe");Assert.IsFalse(Directory.EnumerateFiles(output,"*.lua",SearchOption.AllDirectories).Any());
        }
        using var first=await Run("first","""
            [{"Kind":"tick","Seconds":0},{"Kind":"click","X":240,"Y":260},{"Kind":"click","X":240,"Y":550},{"Kind":"save"}]
            """);
        var selected=first.RootElement.GetProperty("ActionTrace")[3].GetProperty("State").GetProperty("Minigame");
        var state=LuaValue.FromJson(selected.GetProperty("State").GetRawText());var rng=selected.GetProperty("RandomState").GetString();
        Assert.AreEqual(1d,state.Properties!["first"].Number);Assert.AreEqual(0d,state.Properties["moves"].Number);
        var deck=state.Properties["deck"].Items;var mate=Enumerable.Range(2,7).Single(i=>deck[i-1].Number==deck[0].Number);
        var plan=new List<object>{new{Kind="click",X=600,Y=550}};
        void Click(int i)=>plan.Add(new{Kind="click",X=140+(i-1)%4*250+100,Y=220+(i-1)/4*130+40});
        Click(mate);foreach(var group in Enumerable.Range(2,7).Where(i=>i!=mate).GroupBy(i=>deck[i-1].Number))foreach(var i in group)Click(i);
        File.Copy(Path.Combine(directory,"first.save.json"),Path.Combine(directory,"restart.save.json"));
        using var restarted=await Run("restart",JsonSerializer.Serialize(plan));var traces=restarted.RootElement.GetProperty("ActionTrace");
        var restored=traces[0].GetProperty("State").GetProperty("Minigame");Assert.AreEqual(state.ToJson(),LuaValue.FromJson(restored.GetProperty("State").GetRawText()).ToJson());
        Assert.AreEqual(rng,restored.GetProperty("RandomState").GetString());
        Assert.AreNotEqual(first.RootElement.GetProperty("ActionTrace")[0].GetProperty("ScreenInstanceIds").GetRawText(),traces[0].GetProperty("ScreenInstanceIds").GetRawText());
        var final=traces[traces.GetArrayLength()-1].GetProperty("State");Assert.IsFalse(final.TryGetProperty("Minigame",out _));
        Assert.AreEqual(project.Scenes[1].Id,final.GetProperty("SceneId").GetGuid());
        Assert.AreEqual(project.Scenes[1].Lines[0].Texts["ru"],traces[traces.GetArrayLength()-1].GetProperty("Text").GetString());
        Assert.IsTrue(traces[traces.GetArrayLength()-1].GetProperty("PresentedScreenTexts").EnumerateArray().Any(e=>e.GetString()==project.Scenes[1].Lines[0].Texts["ru"]));
        var result=final.GetProperty("MinigameOutcome").GetProperty("Result");Assert.AreEqual(3,result.GetProperty("Phase").GetInt32());
        Assert.AreEqual(4d,result.GetProperty("Data").GetProperty("moves").GetDouble());Assert.AreEqual(96d,result.GetProperty("Data").GetProperty("score").GetDouble());
        Assert.IsTrue(restarted.RootElement.GetProperty("PendingScreenLuaDrawFrames").GetInt32()>0);
        Assert.IsTrue(traces[traces.GetArrayLength()-1].GetProperty("ScreenCount").GetInt32()<=1);

        async Task<JsonDocument> Run(string label,string planText)
        {
            var planFile=Path.Combine(directory,label+".plan.json");File.WriteAllText(planFile,planText);var report=Path.Combine(directory,label+".json");
            var start=new ProcessStartInfo(runtime){WorkingDirectory=directory,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
            if(!packaged)foreach(var arg in new[]{"--project",file,"--assets-root",directory,"--scene",story.Id.ToString(),"--language","ru"})start.ArgumentList.Add(arg);
            else {start.Environment["DOTNET_ROOT"]=Path.Combine(directory,"absent");start.Environment["DOTNET_ROOT_X64"]=Path.Combine(directory,"absent");start.Environment["PATH"]=Environment.GetFolderPath(Environment.SpecialFolder.System);}
            foreach(var arg in new[]{"--smoke-output",Path.Combine(directory,label+".png"),"--smoke-report",report,"--smoke-plan",planFile})start.ArgumentList.Add(arg);
            using var process=Process.Start(start)!;var error=process.StandardError.ReadToEndAsync();var output=process.StandardOutput.ReadToEndAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){process.Kill(true);await process.WaitForExitAsync();Assert.Fail("Mini-game native smoke timed out.");}
            Assert.AreEqual(0,process.ExitCode,await error+await output);var json=JsonDocument.Parse(File.ReadAllText(report));
            Assert.AreEqual(0,json.RootElement.GetProperty("ScreenLuaDiagnostics").GetArrayLength());Assert.AreEqual(0,json.RootElement.GetProperty("Diagnostics").GetArrayLength());
            Assert.IsTrue(json.RootElement.GetProperty("ActionTrace").EnumerateArray().All(e=>e.GetProperty("Success").GetBoolean()));return json;
        }
    }
}
