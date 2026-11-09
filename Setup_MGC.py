"""Windows setup helper. Reuse a webhook locally without running the old bot."""
import ast
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tokenize

WEBHOOK=re.compile(r'https://discord\.com/api/webhooks/[0-9]+/[A-Za-z0-9_-]+')


def read_webhook(path,choose=input):
    try:
        with tokenize.open(path) as stream:tree=ast.parse(stream.read())
    except (SyntaxError,UnicodeError):
        raise RuntimeError('The selected Python file could not be parsed. Choose the original valid bot .py file.') from None
    candidates={}
    for node in ast.walk(tree):
        if isinstance(node,ast.Constant) and isinstance(node.value,str):
            value=node.value.strip()
            if WEBHOOK.fullmatch(value):candidates.setdefault(value,node.lineno)
    values=sorted(candidates,key=candidates.get)
    if not values:
        raise RuntimeError('No literal Discord webhook found. Select the old Python file that actually contains it; environment-variable references alone do not contain the webhook.')
    if len(values)>1:
        print('Several different webhooks found. Their values are hidden.')
        for i,value in enumerate(values,1):print(str(i)+': source line '+str(candidates[value]))
        selected=int(choose('Choose the old bot webhook number: '))-1
        if not 0<=selected<len(values):raise RuntimeError('Invalid webhook selection.')
        return values[selected]
    return values[0]


def choose_old_bot(folder):
    try:
        import tkinter as tk
        from tkinter import filedialog
    except ImportError:
        return Path(input('Full path to your old bot .py file: ').strip().strip('"'))
    window=None
    try:
        window=tk.Tk();window.withdraw();window.attributes('-topmost',True)
        value=filedialog.askopenfilename(parent=window,title='Select your OLD Python bot containing your Discord webhook',initialdir=str(folder),filetypes=[('Python bot','*.py')])
    except tk.TclError:
        value=input('Full path to your old bot .py file: ').strip().strip('"')
    finally:
        if window is not None:window.destroy()
    if not value:raise RuntimeError('Selection canceled; nothing was installed or started.')
    return Path(value)


def verify_package(folder):
    needed=['RunSim101.py','START_SIM.ps1','INSTALL_SIM_BRIDGE.ps1','MANIFEST.json']
    if any(not (folder/name).is_file() for name in needed):
        raise RuntimeError('Save Setup_MGC.py INSIDE the fully extracted MGC_Bot folder, alongside RunSim101.py. Do not run inside the ZIP.')
    manifest=json.loads((folder/'MANIFEST.json').read_text())
    for name,expected in manifest.items():
        relative=Path(name)
        if relative.is_absolute() or '..' in relative.parts:raise RuntimeError('Invalid package path.')
        if hashlib.sha256((folder/relative).read_bytes()).hexdigest()!=expected:
            raise RuntimeError('Package integrity check failed. Extract a fresh complete ZIP.')


def main():
    if sys.platform!='win32':raise RuntimeError('Run this helper on your Windows NinjaTrader computer.')
    folder=Path(__file__).resolve().parent
    verify_package(folder)
    print('Select the OLD bot file once. Its webhook stays hidden and is used only for this run.')
    value=read_webhook(choose_old_bot(folder))
    child_env=os.environ.copy();child_env['DISCORD_WEBHOOK_URL']=value
    print('Webhook found. The old bot was NOT executed. No webhook was saved.')
    print('Disable older Sim101 bots and confirm the account is flat with no working orders.')
    input('Press Enter once Sim101 is flat and the old controllers are closed: ')
    powershell=['powershell.exe','-NoProfile','-ExecutionPolicy','Bypass','-File']
    subprocess.run(powershell+[str(folder/'INSTALL_SIM_BRIDGE.ps1')],cwd=folder,check=True)
    print('\nIn NinjaTrader: New > NinjaScript Editor > F5. There must be ZERO compile errors.')
    print('Open the active MGC expiry: 1 Minute, ETH, DoNotMerge, 60 days.')
    input('After successful compilation and loading the chart, press Enter: ')
    print('\nStarting Python dependencies, Discord delivery check, and the controller.')
    print('After the startup file is written, add/enable SovereignMgcResearchSimBridgeV3 on the chart:')
    print('Sim101 | On bar close | Read launcher startup configuration=True | max contracts=10')
    print('CloseAtNoon=False | evaluation-account switch=False')
    print('Keep this terminal open. A Discord delivery failure stops startup before arming.')
    try:
        subprocess.run(powershell+[str(folder/'START_SIM.ps1'),'-Discord'],cwd=folder,env=child_env,check=True)
    finally:
        child_env.pop('DISCORD_WEBHOOK_URL',None)


if __name__=='__main__':
    try:main()
    except KeyboardInterrupt:
        print('\nStopped. Check NinjaTrader Orders/Positions for actual flatten completion.')
        raise SystemExit(1)
    except RuntimeError as error:
        print('Setup stopped: '+str(error))
        raise SystemExit(1)
    except (OSError,ValueError,subprocess.CalledProcessError):
        # Never render a caught exception that could contain old source text.
        print('Setup stopped. Check the preceding step. No credentials or old source text will be printed.')
        raise SystemExit(1)
