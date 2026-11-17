"""Exercise the real desktop tray lifecycle, including native context-menu commands.

Usage: python scripts/test-system-tray.py --exe "artifacts/CX-Printer-win-x64/CX Printer.exe"
       python scripts/test-system-tray.py --app-id "MasterTea.CXPrinter_3zmdv9shtdpac!App"
The executable form uses the local debug service; neither form changes machine setup.
"""
import argparse
import ctypes
from ctypes import wintypes as w
import json
import os
from pathlib import Path
import subprocess
import time
import uuid

u=ctypes.WinDLL("user32",use_last_error=True)
shell=ctypes.WinDLL("shell32")
k=ctypes.WinDLL("kernel32",use_last_error=True)
u.EnumWindows.argtypes=[ctypes.c_void_p,w.LPARAM]
u.GetWindowThreadProcessId.argtypes=[w.HWND,ctypes.POINTER(w.DWORD)]
u.GetWindowTextW.argtypes=[w.HWND,w.LPWSTR,ctypes.c_int]
u.GetClassNameW.argtypes=[w.HWND,w.LPWSTR,ctypes.c_int]
u.IsWindowVisible.argtypes=[w.HWND]; u.IsIconic.argtypes=[w.HWND]; u.IsZoomed.argtypes=[w.HWND]
u.ShowWindowAsync.argtypes=[w.HWND,ctypes.c_int]
u.PostMessageW.argtypes=[w.HWND,w.UINT,w.WPARAM,w.LPARAM]
u.SendMessageW.argtypes=[w.HWND,w.UINT,w.WPARAM,w.LPARAM]; u.SendMessageW.restype=w.LPARAM
u.GetMenuStringW.argtypes=[w.HMENU,w.UINT,w.LPWSTR,ctypes.c_int,w.UINT]
u.GetMenuItemRect.argtypes=[w.HWND,w.HMENU,w.UINT,ctypes.POINTER(w.RECT)]
u.GetCursorPos.argtypes=[ctypes.POINTER(w.POINT)]
u.SetCursorPos.argtypes=[ctypes.c_int,ctypes.c_int]
k.OpenProcess.argtypes=[w.DWORD,w.BOOL,w.DWORD]; k.OpenProcess.restype=w.HANDLE
k.GetExitCodeProcess.argtypes=[w.HANDLE,ctypes.POINTER(w.DWORD)]
k.CloseHandle.argtypes=[w.HANDLE]


class Guid(ctypes.Structure):
    _fields_=[("data",ctypes.c_ubyte*16)]
    @classmethod
    def make(cls,value): return cls.from_buffer_copy(uuid.UUID(value).bytes_le)


class IconIdentifier(ctypes.Structure):
    _fields_=[("size",w.DWORD),("window",w.HWND),("id",w.UINT),("guid",Guid)]


shell.Shell_NotifyIconGetRect.argtypes=[ctypes.POINTER(IconIdentifier),ctypes.POINTER(w.RECT)]
shell.Shell_NotifyIconGetRect.restype=ctypes.c_long


def wait_for(condition,message,timeout=15):
    deadline=time.monotonic()+timeout
    while time.monotonic()<deadline:
        result=condition()
        if result: return result
        time.sleep(.1)
    raise RuntimeError(message)


def windows_for(pid,menu=False):
    found=[]
    @ctypes.WINFUNCTYPE(w.BOOL,w.HWND,w.LPARAM)
    def visit(window,param):
        owner=w.DWORD(); u.GetWindowThreadProcessId(window,ctypes.byref(owner))
        if owner.value!=pid or not u.IsWindowVisible(window): return True
        text=ctypes.create_unicode_buffer(512)
        (u.GetClassNameW if menu else u.GetWindowTextW)(window,text,len(text))
        if text.value==("#32768" if menu else "CX Printer"): found.append(window)
        return True
    u.EnumWindows(visit,0)
    return found


def activate(app_id):
    ole=ctypes.OleDLL("ole32")
    ole.CoInitializeEx(None,2)
    clsid=Guid.make("45ba127d-10a8-46ea-8ab7-56ea9078943c")
    iid=Guid.make("2e941141-7f97-4756-ba1d-9decde894a3d")
    instance=ctypes.c_void_p()
    ole.CoCreateInstance(ctypes.byref(clsid),None,1,ctypes.byref(iid),ctypes.byref(instance))
    table=ctypes.cast(instance,ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    method=ctypes.WINFUNCTYPE(ctypes.c_long,ctypes.c_void_p,w.LPCWSTR,w.LPCWSTR,w.DWORD,ctypes.POINTER(w.DWORD))(table[3])
    process_id=w.DWORD()
    try:
        result=method(instance,app_id,None,0,ctypes.byref(process_id))
        if result<0: raise RuntimeError(f"Application activation failed: 0x{result&0xffffffff:08x}")
        return process_id.value
    finally:
        ctypes.WINFUNCTYPE(w.ULONG,ctypes.c_void_p)(table[2])(instance)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    source=parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--exe"); source.add_argument("--app-id")
    parser.add_argument("--report")
    args=parser.parse_args()
    process=None
    if args.exe:
        environment=os.environ.copy(); environment["CXPRINTER_DEBUG_SERVICE"]="1"
        process=subprocess.Popen([str(Path(args.exe).resolve())],env=environment,stdin=subprocess.DEVNULL)
        pid=process.pid
    else: pid=activate(args.app_id)
    handle=k.OpenProcess(0x101000,False,pid)
    if not handle: raise ctypes.WinError(ctypes.get_last_error())
    window=None
    cursor=w.POINT(); u.GetCursorPos(ctypes.byref(cursor))
    result={"processId":pid,"launch":args.app_id or args.exe,"checks":[]}
    def check(name): result["checks"].append(name); print("PASS",name,flush=True)
    def exit_code():
        code=w.DWORD(); k.GetExitCodeProcess(handle,ctypes.byref(code)); return code.value
    try:
        window=wait_for(lambda:windows_for(pid),"No visible main window")[0]
        identifier=IconIdentifier(ctypes.sizeof(IconIdentifier),window,1,Guid())
        def icon_exists():
            rectangle=w.RECT()
            # S_FALSE is still a successful HRESULT (Explorer may place the icon in overflow).
            return shell.Shell_NotifyIconGetRect(ctypes.byref(identifier),ctypes.byref(rectangle))>=0
        rectangle=w.RECT()
        icon_result=shell.Shell_NotifyIconGetRect(ctypes.byref(identifier),ctypes.byref(rectangle))
        print(f"Tray identity: hwnd={window:#x}, structure={ctypes.sizeof(identifier)}, query HRESULT={icon_result&0xffffffff:#x}",flush=True)
        wait_for(icon_exists,"Tray icon was not registered with Explorer")
        check("Explorer has the CX Printer tray icon")
        # Let initial asynchronous service/status setup finish before resizing the window.
        time.sleep(2)
        u.ShowWindowAsync(window,6)
        wait_for(lambda:not u.IsWindowVisible(window),"Minimize did not hide the taskbar window")
        assert exit_code()==259 and icon_exists()
        check("Minimize hides the window while the process and tray remain alive")
        u.PostMessageW(window,0x8001,0,(1<<16)|0x0203)
        wait_for(lambda:u.IsWindowVisible(window) and not u.IsIconic(window),"Double-click did not restore the window")
        check("Tray double-click restores the window")
        u.ShowWindowAsync(window,3)
        wait_for(lambda:u.IsZoomed(window),"Maximize failed")
        u.ShowWindowAsync(window,6)
        wait_for(lambda:not u.IsWindowVisible(window),"Maximized window did not minimize to tray")
        u.PostMessageW(window,0x8001,0,(1<<16)|0x0203)
        wait_for(lambda:u.IsWindowVisible(window) and u.IsZoomed(window),"Tray restore lost maximized state")
        check("Tray restore preserves maximized state")
        def click_menu(index):
            u.PostMessageW(window,0x8001,0,(1<<16)|0x007b)
            popup=wait_for(lambda:windows_for(pid,menu=True),"Tray context menu did not open")[0]
            menu=u.SendMessageW(popup,0x01e1,0,0) # MN_GETHMENU.
            labels=[]
            for position in (0,2):
                text=ctypes.create_unicode_buffer(128)
                u.GetMenuStringW(menu,position,text,len(text),0x0400)
                labels.append(text.value)
            assert labels==["显示主窗口","退出 CX Printer"],labels
            rectangle=w.RECT()
            if not u.GetMenuItemRect(window,menu,index,ctypes.byref(rectangle)):
                raise ctypes.WinError(ctypes.get_last_error())
            u.SetCursorPos((rectangle.left+rectangle.right)//2,(rectangle.top+rectangle.bottom)//2)
            u.mouse_event(2,0,0,0,0); u.mouse_event(4,0,0,0,0)
        u.ShowWindowAsync(window,6)
        wait_for(lambda:not u.IsWindowVisible(window),"Window did not hide before menu test")
        click_menu(0)
        wait_for(lambda:u.IsWindowVisible(window) and not u.IsIconic(window),"Show-window menu command failed")
        check("Native tray menu shows the expected items and restores the window")
        u.ShowWindowAsync(window,6)
        wait_for(lambda:not u.IsWindowVisible(window),"Window did not hide before exit test")
        click_menu(2)
        wait_for(lambda:exit_code()!=259,"Tray exit command did not close the application")
        assert exit_code()==0,exit_code()
        wait_for(lambda:not icon_exists(),"Exit left a stale registered tray icon")
        check("Tray exit closes cleanly and removes the registered icon")
        result["exitCode"]=exit_code()
        if args.report:
            destination=Path(args.report); destination.parent.mkdir(parents=True,exist_ok=True)
            destination.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding="utf-8")
    finally:
        u.SetCursorPos(cursor.x,cursor.y)
        if window and exit_code()==259:
            u.PostMessageW(window,0x0010,0,0)
            try: wait_for(lambda:exit_code()!=259,"Test window did not close",10)
            except RuntimeError: pass
        k.CloseHandle(handle)
        if process is not None:
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: process.kill(); process.wait()


if __name__=="__main__": main()
