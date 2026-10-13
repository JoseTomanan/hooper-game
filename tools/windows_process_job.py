"""Own a Windows peer tree even after its original process exits."""

import ctypes
from ctypes import wintypes
import os
import subprocess


CREATE_SUSPENDED = 0x00000004
JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000
JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9
PROCESS_SET_QUOTA = 0x0100
PROCESS_TERMINATE = 0x0001
THREAD_SUSPEND_RESUME = 0x0002
TH32CS_SNAPTHREAD = 0x00000004
ERROR_NO_MORE_FILES = 18
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value


# SIZE_T and ULONG_PTR follow pointer width; LARGE_INTEGER is always 64 bits.
# https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information
class _BasicLimitInformation(ctypes.Structure):
    _fields_ = [
        ('PerProcessUserTimeLimit', ctypes.c_longlong),
        ('PerJobUserTimeLimit', ctypes.c_longlong),
        ('LimitFlags', wintypes.DWORD),
        ('MinimumWorkingSetSize', ctypes.c_size_t),
        ('MaximumWorkingSetSize', ctypes.c_size_t),
        ('ActiveProcessLimit', wintypes.DWORD),
        ('Affinity', ctypes.c_size_t),
        ('PriorityClass', wintypes.DWORD),
        ('SchedulingClass', wintypes.DWORD),
    ]


class _IoCounters(ctypes.Structure):
    _fields_ = [(name, ctypes.c_ulonglong) for name in (
        'ReadOperationCount', 'WriteOperationCount', 'OtherOperationCount',
        'ReadTransferCount', 'WriteTransferCount', 'OtherTransferCount')]


class _ExtendedLimitInformation(ctypes.Structure):
    _fields_ = [
        ('BasicLimitInformation', _BasicLimitInformation),
        ('IoInfo', _IoCounters),
        ('ProcessMemoryLimit', ctypes.c_size_t),
        ('JobMemoryLimit', ctypes.c_size_t),
        ('PeakProcessMemoryUsed', ctypes.c_size_t),
        ('PeakJobMemoryUsed', ctypes.c_size_t),
    ]


# https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/ns-tlhelp32-threadentry32
class _ThreadEntry32(ctypes.Structure):
    _fields_ = [
        ('dwSize', wintypes.DWORD),
        ('cntUsage', wintypes.DWORD),
        ('th32ThreadID', wintypes.DWORD),
        ('th32OwnerProcessID', wintypes.DWORD),
        ('tpBasePri', wintypes.LONG),
        ('tpDeltaPri', wintypes.LONG),
        ('dwFlags', wintypes.DWORD),
    ]


def _kernel32():
    api = ctypes.WinDLL('kernel32', use_last_error=True)
    signatures = {
        'CreateJobObjectW': ([ctypes.c_void_p, wintypes.LPCWSTR], wintypes.HANDLE),
        'SetInformationJobObject': (
            [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD],
            wintypes.BOOL),
        'AssignProcessToJobObject': ([wintypes.HANDLE, wintypes.HANDLE], wintypes.BOOL),
        'TerminateJobObject': ([wintypes.HANDLE, wintypes.UINT], wintypes.BOOL),
        'CloseHandle': ([wintypes.HANDLE], wintypes.BOOL),
        'OpenProcess': ([wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE),
        'CreateToolhelp32Snapshot': ([wintypes.DWORD, wintypes.DWORD], wintypes.HANDLE),
        'Thread32First': ([wintypes.HANDLE, ctypes.POINTER(_ThreadEntry32)], wintypes.BOOL),
        'Thread32Next': ([wintypes.HANDLE, ctypes.POINTER(_ThreadEntry32)], wintypes.BOOL),
        'OpenThread': ([wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE),
        'ResumeThread': ([wintypes.HANDLE], wintypes.DWORD),
    }
    for name, (args, result) in signatures.items():
        function = getattr(api, name)
        function.argtypes = args
        function.restype = result
    return api


def _win_error(operation):
    error = ctypes.WinError(ctypes.get_last_error())
    error.strerror = f'{operation}: {error.strerror}'
    return error


class WindowsProcessJob:
    """Call close at lifecycle cleanup, even when the returned Popen has exited.

    Jobs retain descendants independently of the leader's PID. Assignment before
    resume prevents a fast leader spawning a child outside this containment.
    https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects
    """

    def __init__(self):
        if os.name != 'nt':
            raise OSError('WindowsProcessJob requires Windows')
        self._api = _kernel32()
        self._job = self._api.CreateJobObjectW(None, None)
        if not self._job:
            raise _win_error('CreateJobObjectW')
        limits = _ExtendedLimitInformation()
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not self._api.SetInformationJobObject(
                self._job, JOB_OBJECT_EXTENDED_LIMIT_INFORMATION,
                ctypes.byref(limits), ctypes.sizeof(limits)):
            error = _win_error('SetInformationJobObject')
            self._api.CloseHandle(self._job)
            self._job = None
            raise error

    def _primary_thread(self, pid):
        # Popen closes CreateProcess's thread handle. A suspended new process has
        # one initial thread, discoverable via the documented Tool Help APIs.
        # https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/nf-tlhelp32-thread32first
        snapshot = self._api.CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0)
        if snapshot == INVALID_HANDLE_VALUE:
            raise _win_error('CreateToolhelp32Snapshot')
        try:
            entry = _ThreadEntry32()
            entry.dwSize = ctypes.sizeof(entry)
            available = self._api.Thread32First(snapshot, ctypes.byref(entry))
            while available:
                if entry.th32OwnerProcessID == pid:
                    thread = self._api.OpenThread(
                        THREAD_SUSPEND_RESUME, False, entry.th32ThreadID)
                    if not thread:
                        raise _win_error('OpenThread')
                    return thread
                entry.dwSize = ctypes.sizeof(entry)
                available = self._api.Thread32Next(snapshot, ctypes.byref(entry))
            if ctypes.get_last_error() != ERROR_NO_MORE_FILES:
                raise _win_error('Thread32First/Thread32Next')
            raise OSError(f'No initial thread found for suspended process {pid}')
        finally:
            self._api.CloseHandle(snapshot)

    def launch(self, command: list[str], **kwargs) -> subprocess.Popen:
        if not self._job:
            raise OSError('Windows process job is closed')
        kwargs['creationflags'] = kwargs.get('creationflags', 0) | CREATE_SUSPENDED
        process = None
        process_handle = thread = None
        try:
            process = subprocess.Popen(command, **kwargs)
            process_handle = self._api.OpenProcess(
                PROCESS_SET_QUOTA | PROCESS_TERMINATE, False, process.pid)
            if not process_handle:
                raise _win_error('OpenProcess')
            if not self._api.AssignProcessToJobObject(self._job, process_handle):
                raise _win_error('AssignProcessToJobObject')
            thread = self._primary_thread(process.pid)
            # ResumeThread returns the previous suspend count, or DWORD(-1).
            # https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-resumethread
            previous_count = self._api.ResumeThread(thread)
            if previous_count == 0xffffffff:
                raise _win_error('ResumeThread')
            if previous_count != 1:
                raise OSError(f'Unexpected initial thread suspend count: {previous_count}')
            return process
        except BaseException:
            # This also handles interruption between Popen and assignment. Never
            # leave an uncontained suspended child behind on a failed launch.
            try:
                if process is not None:
                    process.kill()
                    process.wait()
            finally:
                self.close()
            raise
        finally:
            if thread:
                self._api.CloseHandle(thread)
            if process_handle:
                self._api.CloseHandle(process_handle)

    def close(self):
        """Terminate all job members and release the job; safe to call twice."""
        job = self._job
        if not job:
            return
        self._job = None
        error = None
        # Explicit termination reports errors; kill-on-close remains the fallback.
        # https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-terminatejobobject
        if not self._api.TerminateJobObject(job, 1):
            error = _win_error('TerminateJobObject')
        if not self._api.CloseHandle(job) and error is None:
            error = _win_error('CloseHandle(job)')
        if error is not None:
            raise error
