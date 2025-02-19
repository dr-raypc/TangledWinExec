﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ProcAccessCheck.Interop;

namespace ProcAccessCheck.Library
{
    using NTSTATUS = Int32;

    internal class Utilities
    {
        public static bool EnableSinglePrivilege(string privilegeName)
        {
            return EnableSinglePrivilege(WindowsIdentity.GetCurrent().Token, privilegeName);
        }


        public static bool EnableSinglePrivilege(IntPtr hToken, string privilegeName)
        {
            bool status = Helpers.GetPrivilegeLuid(privilegeName, out LUID privilegeLuid);

            if (status)
                status = EnableSinglePrivilege(hToken, privilegeLuid);

            return status;
        }


        public static bool EnableSinglePrivilege(IntPtr hToken, LUID priv)
        {
            int error;
            var tp = new TOKEN_PRIVILEGES(1);
            tp.Privileges[0].Luid = priv;
            tp.Privileges[0].Attributes = (uint)SE_PRIVILEGE_ATTRIBUTES.SE_PRIVILEGE_ENABLED;

            IntPtr pTokenPrivilege = Marshal.AllocHGlobal(Marshal.SizeOf(tp));
            Marshal.StructureToPtr(tp, pTokenPrivilege, true);

            NativeMethods.AdjustTokenPrivileges(
                hToken,
                false,
                pTokenPrivilege,
                0,
                out TOKEN_PRIVILEGES _,
                out int _);
            error = Marshal.GetLastWin32Error();

            return (error == Win32Consts.ERROR_SUCCESS);
        }


        public static bool EnableMultiplePrivileges(IntPtr hToken, string[] privs)
        {
            bool isEnabled;
            bool enabledAll = true;
            var opt = StringComparison.OrdinalIgnoreCase;
            var results = new Dictionary<string, bool>();
            var privList = new List<string>(privs);
            var availablePrivs = GetAvailablePrivileges(hToken);

            foreach (var name in privList)
                results.Add(name, false);

            foreach (var priv in availablePrivs)
            {
                foreach (var name in privList)
                {
                    if (string.Compare(Helpers.GetPrivilegeName(priv.Key), name, opt) == 0)
                    {
                        isEnabled = ((priv.Value & (uint)SE_PRIVILEGE_ATTRIBUTES.SE_PRIVILEGE_ENABLED) != 0);

                        if (isEnabled)
                            results[name] = true;
                        else
                            results[name] = EnableSinglePrivilege(hToken, priv.Key);
                    }
                }
            }

            foreach (var result in results)
            {
                if (!result.Value)
                {
                    Console.WriteLine("[-] {0} is not available.", result.Key);
                    enabledAll = false;
                }
            }

            return enabledAll;
        }


        public static Dictionary<LUID, uint> GetAvailablePrivileges(IntPtr hToken)
        {
            int error;
            bool status;
            int nPriviliegeCount;
            IntPtr pTokenPrivileges;
            IntPtr pPrivilege;
            LUID_AND_ATTRIBUTES luidAndAttributes;
            int nluidAttributesSize = Marshal.SizeOf(typeof(LUID_AND_ATTRIBUTES));
            int bufferLength = Marshal.SizeOf(typeof(TOKEN_PRIVILEGES));
            var availablePrivs = new Dictionary<LUID, uint>();

            do
            {
                pTokenPrivileges = Marshal.AllocHGlobal(bufferLength);
                Helpers.ZeroMemory(pTokenPrivileges, bufferLength);

                status = NativeMethods.GetTokenInformation(
                    hToken,
                    TOKEN_INFORMATION_CLASS.TokenPrivileges,
                    pTokenPrivileges,
                    bufferLength,
                    out bufferLength);
                error = Marshal.GetLastWin32Error();

                if (!status)
                    Marshal.FreeHGlobal(pTokenPrivileges);
            } while (!status && (error == Win32Consts.ERROR_INSUFFICIENT_BUFFER));

            if (!status)
                return availablePrivs;

            nPriviliegeCount = Marshal.ReadInt32(pTokenPrivileges);
            pPrivilege = new IntPtr(pTokenPrivileges.ToInt64() + Marshal.SizeOf(nPriviliegeCount));

            for (var count = 0; count < nPriviliegeCount; count++)
            {
                luidAndAttributes = (LUID_AND_ATTRIBUTES)Marshal.PtrToStructure(
                    pPrivilege,
                    typeof(LUID_AND_ATTRIBUTES));
                availablePrivs.Add(luidAndAttributes.Luid, luidAndAttributes.Attributes);

                if (Environment.Is64BitProcess)
                    pPrivilege = new IntPtr(pPrivilege.ToInt64() + nluidAttributesSize);
                else
                    pPrivilege = new IntPtr(pPrivilege.ToInt32() + nluidAttributesSize);
            }

            Marshal.FreeHGlobal(pTokenPrivileges);

            return availablePrivs;
        }


        public static bool GetProcessHandleAccess(
            int pid,
            IntPtr hProcess,
            out ACCESS_MASK accessMask)
        {
            NTSTATUS ntstatus;
            IntPtr pSystemInformation;
            IntPtr pEntry;
            ulong nEntryCount;
            uint OBJ_GRANTED_ACCESS_MASK = 0x01FFFFFF;
            int nEntrySize = Marshal.SizeOf(typeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));
            int nSystemInformationLength = 1024;
            int offset = Environment.Is64BitProcess ? 0x10 : 0x8; // Offset for SYSTEM_HANDLE_INFORMATION_EX.Handles
            var found = false;
            accessMask = ACCESS_MASK.NO_ACCESS;

            do
            {
                do
                {
                    pSystemInformation = Marshal.AllocHGlobal(nSystemInformationLength);

                    ntstatus = NativeMethods.NtQuerySystemInformation(
                        SYSTEM_INFORMATION_CLASS.SystemExtendedHandleInformation,
                        pSystemInformation,
                        nSystemInformationLength,
                        ref nSystemInformationLength);

                    if (ntstatus != Win32Consts.STATUS_SUCCESS)
                    {
                        Marshal.FreeHGlobal(pSystemInformation);
                        pSystemInformation = IntPtr.Zero;
                    }
                } while (ntstatus == Win32Consts.STATUS_INFO_LENGTH_MISMATCH);

                if (ntstatus != Win32Consts.STATUS_SUCCESS)
                    break;

                nEntryCount = (ulong)Marshal.ReadIntPtr(pSystemInformation);

                if (Environment.Is64BitProcess)
                    pEntry = new IntPtr(pSystemInformation.ToInt64() + offset);
                else
                    pEntry = new IntPtr(pSystemInformation.ToInt32() + offset);

                for (var count = 0UL; count < nEntryCount; count++)
                {
                    var entry = (SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX)Marshal.PtrToStructure(
                        pEntry,
                        typeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));

                    if ((int)entry.UniqueProcessId == pid)
                    {
                        if ((long)entry.HandleValue.ToUInt64() == hProcess.ToInt64())
                        {
                            Console.WriteLine("pEntry @ 0x{0}", pEntry.ToString("X16"));
                            Console.ReadLine();
                            accessMask = (ACCESS_MASK)(entry.GrantedAccess & OBJ_GRANTED_ACCESS_MASK);
                            found = true;
                            break;
                        }
                    }

                    if (Environment.Is64BitProcess)
                        pEntry = new IntPtr(pEntry.ToInt64() + nEntrySize);
                    else
                        pEntry = new IntPtr(pEntry.ToInt32() + nEntrySize);
                }
            } while (false);

            if (pSystemInformation != IntPtr.Zero)
                Marshal.FreeHGlobal(pSystemInformation);

            return found;
        }


        public static bool ImpersonateAsWinlogon()
        {
            return ImpersonateAsWinlogon(new string[] { });
        }


        public static bool ImpersonateAsWinlogon(string[] privs)
        {
            int error;
            int winlogon;
            bool status;
            IntPtr hProcess;
            IntPtr hToken;
            IntPtr hDupToken = IntPtr.Zero;
            var privileges = new string[] { Win32Consts.SE_DEBUG_NAME, Win32Consts.SE_IMPERSONATE_NAME };

            try
            {
                winlogon = (Process.GetProcessesByName("winlogon")[0]).Id;
            }
            catch
            {
                Console.WriteLine("[-] Failed to get PID of winlogon.exe.");

                return false;
            }

            status = EnableMultiplePrivileges(WindowsIdentity.GetCurrent().Token, privileges);

            if (!status)
            {
                Console.WriteLine("[-] Insufficient privilege.");

                return false;
            }

            hProcess = NativeMethods.OpenProcess(
                ACCESS_MASK.PROCESS_QUERY_LIMITED_INFORMATION,
                true,
                winlogon);

            if (hProcess == IntPtr.Zero)
            {
                error = Marshal.GetLastWin32Error();
                Console.WriteLine("[-] Failed to get handle to winlogon.exe process.");
                Console.WriteLine("    |-> {0}", Helpers.GetWin32ErrorMessage(error, false));

                return false;
            }

            do
            {
                status = NativeMethods.OpenProcessToken(
                    hProcess,
                    TokenAccessFlags.TOKEN_DUPLICATE,
                    out hToken);

                if (!status)
                {
                    error = Marshal.GetLastWin32Error();
                    Console.WriteLine("[-] Failed to get handle to smss.exe process token.");
                    Console.WriteLine("    |-> {0}", Helpers.GetWin32ErrorMessage(error, false));
                    hToken = IntPtr.Zero;

                    break;
                }

                status = NativeMethods.DuplicateTokenEx(
                    hToken,
                    TokenAccessFlags.MAXIMUM_ALLOWED,
                    IntPtr.Zero,
                    SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                    TOKEN_TYPE.TokenPrimary,
                    out hDupToken);

                if (!status)
                {
                    error = Marshal.GetLastWin32Error();
                    Console.WriteLine("[-] Failed to duplicate winlogon.exe process token.");
                    Console.WriteLine("    |-> {0}", Helpers.GetWin32ErrorMessage(error, false));

                    break;
                }

                if (privs.Length > 0)
                {
                    status = EnableMultiplePrivileges(hDupToken, privs);

                    if (!status)
                        break;
                }

                status = ImpersonateThreadToken(hDupToken);
            } while (false);

            if (hToken != IntPtr.Zero)
                NativeMethods.NtClose(hToken);

            if (hDupToken != IntPtr.Zero)
                NativeMethods.NtClose(hDupToken);

            NativeMethods.NtClose(hProcess);

            return status;
        }


        public static bool ImpersonateThreadToken(IntPtr hImpersonationToken)
        {
            int error;
            IntPtr hCurrentToken;
            SECURITY_IMPERSONATION_LEVEL impersonationLevel;

            if (!NativeMethods.ImpersonateLoggedOnUser(hImpersonationToken))
            {
                error = Marshal.GetLastWin32Error();
                Console.WriteLine("[-] Failed to impersonation.");
                Console.WriteLine("    |-> {0}", Helpers.GetWin32ErrorMessage(error, false));

                return false;
            }

            hCurrentToken = WindowsIdentity.GetCurrent().Token;
            Helpers.GetInformationFromToken(
                hCurrentToken,
                TOKEN_INFORMATION_CLASS.TokenImpersonationLevel,
                out IntPtr pImpersonationLevel);
            impersonationLevel = (SECURITY_IMPERSONATION_LEVEL)Marshal.ReadInt32(pImpersonationLevel);
            Marshal.FreeHGlobal(pImpersonationLevel);

            return (impersonationLevel != SECURITY_IMPERSONATION_LEVEL.SecurityIdentification);
        }


        public static bool IsPrivilegeAvailable(string privilegeName)
        {
            return IsPrivilegeAvailable(WindowsIdentity.GetCurrent().Token, privilegeName);
        }


        public static bool IsPrivilegeAvailable(IntPtr hToken, string privilegeName)
        {
            string entryName;
            bool isAvailable = false;
            Dictionary<LUID, uint> privs = GetAvailablePrivileges(hToken);

            foreach (var priv in privs)
            {
                entryName = Helpers.GetPrivilegeName(priv.Key);

                if (Helpers.CompareIgnoreCase(entryName, privilegeName))
                {
                    isAvailable = true;

                    break;
                }
            }

            return isAvailable;
        }
    }
}
