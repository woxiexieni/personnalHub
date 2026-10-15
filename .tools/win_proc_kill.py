import ctypes
from ctypes import wintypes
import sys

TH32CS_SNAPPROCESS = 0x00000002
PROCESS_TERMINATE = 0x0001
PROCESS_QUERY_INFORMATION = 0x0400

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)


class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ProcessID", wintypes.DWORD),
        ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)),
        ("th32ModuleID", wintypes.DWORD),
        ("cntThreads", wintypes.DWORD),
        ("th32ParentProcessID", wintypes.DWORD),
        ("pcPriClassBase", ctypes.c_long),
        ("dwFlags", wintypes.DWORD),
        ("szExeFile", ctypes.c_char * 260),
    ]


def enum_procs():
    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == -1 or snap == 0xFFFFFFFF:
        raise ctypes.WinError(ctypes.get_last_error())
    entry = PROCESSENTRY32()
    entry.dwSize = ctypes.sizeof(PROCESSENTRY32)
    ok = kernel32.Process32First(snap, ctypes.byref(entry))
    out = []
    while ok:
        out.append((entry.th32ProcessID, entry.th32ParentProcessID,
                    entry.szExeFile.decode("mbcs", "replace")))
        ok = kernel32.Process32Next(snap, ctypes.byref(entry))
    kernel32.CloseHandle(snap)
    return out


KEYS = [k.lower() for k in sys.argv[1:]] or ["personalhub"]
print("pid\tppid\tname")
targets = []
for pid, ppid, name in enum_procs():
    low = name.lower()
    if any(k in low for k in KEYS):
        print(f"{pid}\t{ppid}\t{name}")
        targets.append((pid, name))

print("--- kill ---")
for pid, name in targets:
    h = kernel32.OpenProcess(PROCESS_TERMINATE, False, pid)
    if not h:
        err = ctypes.get_last_error()
        print(f"pid={pid} {name}: OpenProcess failed err={err} ({ctypes.FormatError(err)})")
        continue
    if kernel32.TerminateProcess(h, 1):
        print(f"pid={pid} {name}: terminated")
    else:
        err = ctypes.get_last_error()
        print(f"pid={pid} {name}: TerminateProcess failed err={err} ({ctypes.FormatError(err)})")
    kernel32.CloseHandle(h)
