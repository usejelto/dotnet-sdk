#!/usr/bin/env python3
"""Compile the actual docs blocks and consume the nupkg, with real framework references."""
import argparse
import os
from pathlib import Path
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument('--framework', choices=['net8.0', 'net10.0'], default='net8.0')
parser.add_argument('--desktop', action='store_true', help='also restore and compile WPF, WinForms and Avalonia')
parser.add_argument('--registry', action='store_true', help='consume this exact version from NuGet.org')
options = parser.parse_args()
cli = os.environ.get('DOTNET', 'dotnet')
feed = root / 'artifacts'
version = ET.parse(root / 'Jelto/Jelto.csproj').findtext('.//Version')
if not options.registry and not (feed / f'Jelto.{version}.nupkg').is_file():
    raise SystemExit('Run make package in the SDK directory first.')
blocks = re.findall(r'```csharp\n(.*?)\n```', (root / 'vendor/jelto/dotnet.md').read_text(), re.S)
if not blocks:
    raise SystemExit('The pinned guide must contain C# examples to verify.')
env = {**os.environ, 'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1'}
with tempfile.TemporaryDirectory(prefix='jelto-dotnet-consumer-') as temp:
    scratch = Path(temp)
    # A fresh NuGet cache guarantees consumption of this build even at the same package version.
    env['NUGET_PACKAGES'] = str(scratch / 'packages')
    def project(name, source, kind='console'):
        directory = scratch / name
        directory.mkdir()
        target = options.framework + ('-windows' if kind in ('wpf', 'winforms') else '')
        framework = {'wpf': '<UseWPF>true</UseWPF><EnableWindowsTargeting>true</EnableWindowsTargeting>',
                     'winforms': '<UseWindowsForms>true</UseWindowsForms><EnableWindowsTargeting>true</EnableWindowsTargeting>'}.get(kind, '')
        output = 'Library' if kind in ('wpf', 'avalonia') else 'WinExe' if kind == 'winforms' else 'Exe'
        references = f'<PackageReference Include="Jelto" Version="[{version}]" />'
        if kind == 'avalonia': references += '<PackageReference Include="Avalonia" Version="11.3.0" />'
        (directory / 'Sample.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>{target}</TargetFramework><OutputType>{output}</OutputType>
<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<NuGetAudit>false</NuGetAudit>{framework}</PropertyGroup><ItemGroup>{references}</ItemGroup></Project>''')
        (directory / 'Program.cs').write_text(source)
        sources = [] if options.registry else ['--source', str(feed)]
        subprocess.run([cli, 'restore', str(directory), *sources, '--source', 'https://api.nuget.org/v3/index.json'], env=env, check=True)
        subprocess.run([cli, 'build', str(directory), '-c', 'Release', '--no-restore', '-p:UseSharedCompilation=false'], env=env, check=True)
        return directory
    for index, block in enumerate(blocks):
        # The guide contains snippets for an existing app, so supply its namespace
        # when a block continues the earlier initialization example.
        source = block if re.search(r'^using Jelto;', block, re.M) else 'using Jelto;\n' + block
        project('docs-' + str(index), source)
    if options.desktop:
        # Compile the same statements in real framework startup methods. Keep
        # imports outside the class instead of relying on code-block positions.
        statements = '\n'.join(blocks)
        imports = '\n'.join(dict.fromkeys(re.findall(r'^using [^;]+;', statements, re.M)))
        statements = re.sub(r'^using [^;]+;\s*', '', statements, flags=re.M)
        hosts = {
            'wpf': 'public class App : System.Windows.Application { protected override void OnStartup(System.Windows.StartupEventArgs e) { base.OnStartup(e); %s } }',
            'winforms': 'public static class Program { [System.STAThread] public static void Main() { %s System.Windows.Forms.Application.Run(new System.Windows.Forms.Form()); } }',
            'avalonia': 'public class App : Avalonia.Application { public override void Initialize() { %s } }',
        }
        for kind, host in hosts.items():
            project('docs-' + kind, imports + '\n' + host % statements, kind)
    consumer = project('consumer', '''using Jelto;
using System.Runtime.InteropServices;
JeltoClient.Initialize("prd_conform001", endpoint: "http://127.0.0.1:1/v1/e");
var id = JeltoClient.InstallId;
if (!Guid.TryParse(id, out _) || id[14] != '4') throw new Exception("persistent UUIDv4 missing");
JeltoClient.Track("consumer", new Dictionary<string, object?> { ["number"] = 29.90m });
JeltoClient.SetProps(new Dictionary<string, string> { ["license"] = "paid" });
JeltoClient.Onboarding("setup", "ok");
JeltoClient.Reset();
if (JeltoClient.InstallId == id) throw new Exception("reset did not rotate");
JeltoClient.Disable();
if (JeltoClient.InstallId != "") throw new Exception("disable retained identity");
if (Directory.EnumerateFileSystemEntries(Environment.GetEnvironmentVariable("JELTO_STATE_DIR")!).Any()) throw new Exception("disable retained state");
Console.WriteLine($"Local NuGet consumer PASS: .NET {Environment.Version}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");
''')
    env['JELTO_STATE_DIR'] = str(scratch / 'state')
    subprocess.run([cli, 'run', '--project', str(consumer), '-c', 'Release', '--no-build'], env=env, check=True)
    print(f'PASS {options.framework}: local package consumer and {len(blocks)} documentation blocks'
          + (' in console, WPF, WinForms and Avalonia' if options.desktop else ''))
