#!/usr/bin/env python3
"""Execute a task candidate with build inputs and SDK/PowerShell paths removed."""
import json
import os
from pathlib import Path
import shutil
import sys

root = Path(os.environ['SHOU_DEV_ROOT']).resolve()
command = sys.argv[1:]
if command[:1] == ['--']:
    command = command[1:]
if not command or not Path(command[0]).is_absolute():
    raise ValueError('An absolute candidate executable is required')
executable = Path(command[0]).resolve(strict=True)
if not executable.is_relative_to(root):
    raise ValueError('Candidate must remain under the registered task root')
environment = dict(os.environ)
aliases = {'CURSEFORGE_API_KEY', 'CURSEFORGE_API', 'MS_CLIENT_ID', 'CLIENT_ID', 'MSBuildSDKsPath'}
removed = []
for name in list(environment):
    if name.startswith(('PCL_', 'DOTNET_', 'MSBUILD')) or name in aliases:
        removed.append(name)
        del environment[name]
# This changes only the owned child environment, never the system or shell profile.
environment['PATH'] = '/usr/bin:/bin:/usr/sbin:/sbin' if os.name != 'nt' else str(Path(environment['SystemRoot']) / 'System32')
environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
print(json.dumps({'scope': 'clean_candidate_environment', 'pcl_remaining': any(n.startswith('PCL_') for n in environment),
    'sdk_on_path': shutil.which('dotnet', path=environment['PATH']) is not None,
    'pwsh_on_path': shutil.which('pwsh', path=environment['PATH']) is not None,
    'env_file_in_program_directory': (executable.parent / '.env').exists(), 'removed_input_count': len(removed)}), flush=True)
os.execve(executable, command, environment)
