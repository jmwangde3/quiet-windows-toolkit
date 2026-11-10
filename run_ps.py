# -*- coding: utf-8 -*-
"""Run a PowerShell command safely on zh-CN Windows.

Chinese paths and nested quotes survive because the script text is passed as
Base64 of UTF-16LE (-EncodedCommand), never as a raw -Command argument.
A prelude normalizes stdin/stdout encoding so an UTF-8 host decodes output.

Usage (from Bash / Git Bash, which has a clean UTF-8 argv chain):
  python run_ps.py 'Write-Output "zh probe"; Get-ChildItem -LiteralPath "C:/<中文路径>/probe.txt"'
  python run_ps.py - < script.ps1
"""
import base64
import os
import subprocess
import sys

PRELUDE = (
    "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;"
    "$OutputEncoding=[System.Text.Encoding]::UTF8;"
    "$ErrorActionPreference='Stop';"
    "$ProgressPreference='SilentlyContinue';"
)


def powershell_exe():
    root = os.environ.get("SystemRoot", r"C:\Windows")
    candidates = (
        os.path.join(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
        os.path.join(root, "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe"),
    )
    for path in candidates:
        if os.path.isfile(path):
            return path
    return "powershell.exe"


def run(script, timeout=300):
    payload = PRELUDE + script
    encoded = base64.b64encode(payload.encode("utf-16-le")).decode("ascii")
    argv = [
        powershell_exe(),
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy",
        "Bypass",
        "-EncodedCommand",
        encoded,
    ]
    return subprocess.run(argv, capture_output=True, timeout=timeout)


def main():
    if len(sys.argv) > 1:
        script = sys.argv[1]
    else:
        script = sys.stdin.read()
    result = run(script)
    sys.stdout.buffer.write(result.stdout)
    sys.stderr.buffer.write(result.stderr)
    return result.returncode


if __name__ == "__main__":
    sys.exit(main())
