﻿/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Timers;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class Connection : SteamHandler, IDisposable
    {
        public const uint RETRY_DELAY = 15;

        public static DateTime LastSuccessfulLogin { get; private set; }

        private Timer ReconnectionTimer;

        private string AuthCode;
        private bool IsTwoFactor;

        public Connection(CallbackManager manager)
        {
            ReconnectionTimer = new Timer
            {
                AutoReset = false
            };
            ReconnectionTimer.Elapsed += Reconnect;
            ReconnectionTimer.Interval = TimeSpan.FromSeconds(RETRY_DELAY).TotalMilliseconds;

            manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
            manager.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);
            manager.Subscribe<SteamUser.LoginKeyCallback>(OnLoginKey);
        }

        public void Dispose()
        {
            if (ReconnectionTimer != null)
            {
                ReconnectionTimer.Dispose();
                ReconnectionTimer = null;
            }
        }

        public static void Reconnect(object sender, ElapsedEventArgs e)
        {
            Log.WriteDebug(nameof(Steam), "Reconnecting...");

            Steam.Instance.Client.Connect();
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            ReconnectionTimer.Stop();

            Log.WriteInfo(nameof(Steam), $"Connected, logging in to cellid {LocalConfig.Current.CellID}...");

            if (Settings.Current.Steam.Username == "anonymous")
            {
                Log.WriteInfo(nameof(Steam), "Using an anonymous account");

                Steam.Instance.User.LogOnAnonymous(new SteamUser.AnonymousLogOnDetails
                {
                    CellID = LocalConfig.Current.CellID,
                });

                return;
            }

            byte[] sentryHash = null;

            if (LocalConfig.Current.Sentry != null)
            {
                sentryHash = CryptoHelper.SHAHash(LocalConfig.Current.Sentry);
            }

            Steam.Instance.User.LogOn(new SteamUser.LogOnDetails
            {
                Username = Settings.Current.Steam.Username,
                Password = Settings.Current.Steam.Password,
                CellID = LocalConfig.Current.CellID,
                AuthCode = IsTwoFactor ? null : AuthCode,
                TwoFactorCode = IsTwoFactor ? AuthCode : null,
                SentryFileHash = sentryHash,
                ShouldRememberPassword = true,
                LoginKey = LocalConfig.Current.LoginKey,
                LoginID = 0x78_50_61_77,
            });
            AuthCode = null;
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Steam.Instance.PICSChanges.StopTick();

            if (!Steam.Instance.IsRunning)
            {
                Log.WriteInfo(nameof(Steam), "Disconnected from Steam");

                return;
            }
            
            Log.WriteInfo(nameof(Steam), $"Disconnected from Steam. Retrying in {RETRY_DELAY} seconds... {(callback.UserInitiated ? " (user initiated)" : "")}");

            IRC.Instance.SendEmoteAnnounce($"disconnected from Steam. Retrying in {RETRY_DELAY} seconds…");

            ReconnectionTimer.Start();
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.AccountLogonDenied)
            {
                Console.Write($"STEAM GUARD! Please enter the auth code sent to the email at {callback.EmailDomain}: ");

                IsTwoFactor = false;
                AuthCode = Console.ReadLine()?.Trim();

                return;
            }
            else if (callback.Result == EResult.AccountLoginDeniedNeedTwoFactor)
            {
                Console.Write("STEAM GUARD! Please enter your 2 factor auth code from your authenticator app: ");

                IsTwoFactor = true;
                AuthCode = Console.ReadLine()?.Trim();

                return;
            }

            if (callback.Result == EResult.InvalidPassword)
            {
                LocalConfig.Current.LoginKey = null;
            }

            if (callback.Result != EResult.OK)
            {
                Log.WriteInfo(nameof(Steam), $"Failed to login: {callback.Result}");

                IRC.Instance.SendEmoteAnnounce($"failed to log in: {callback.Result}");

                return;
            }

            var cellId = callback.CellID;

            if (LocalConfig.Current.CellID != cellId)
            {
                LocalConfig.Current.CellID = cellId;
                LocalConfig.Save();
            }

            LastSuccessfulLogin = DateTime.Now;

            Log.WriteInfo(nameof(Steam), $"Logged in, current Valve time is {callback.ServerTime:R}");

            IRC.Instance.SendEmoteAnnounce($"logged in. Valve time: {callback.ServerTime:R}");

            JobManager.RestartJobsIfAny();

            if (Settings.IsFullRun)
            {
                if (Settings.Current.FullRun == FullRunState.ImportantOnly)
                {
                    JobManager.AddJob(
                        () => Steam.Instance.Apps.PICSGetAccessTokens(Application.ImportantApps.Keys, Application.ImportantSubs.Keys),
                        new PICSTokens.RequestedTokens
                        {
                            Apps = Application.ImportantApps.Keys.ToList(),
                            Packages = Application.ImportantSubs.Keys.ToList(),
                        });
                }
                else if (Steam.Instance.PICSChanges.PreviousChangeNumber == 0)
                {
                    TaskManager.Run(FullUpdateProcessor.PerformSync);
                }
            }
            else
            {
                Steam.Instance.PICSChanges.StartTick();
            }
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Log.WriteInfo(nameof(Steam), $"Logged out of Steam: {callback.Result}");

            IRC.Instance.SendEmoteAnnounce($"logged out of Steam: {callback.Result}");
        }

        private void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback)
        {
            Log.WriteInfo(nameof(Steam), $"Updating sentry file... {callback.FileName}");

            if (callback.Data.Length != callback.BytesToWrite)
            {
                ErrorReporter.Notify(nameof(Steam), new InvalidDataException($"Data.Length ({callback.Data.Length}) != BytesToWrite ({callback.BytesToWrite}) in OnMachineAuth"));
            }

            using (var stream = new MemoryStream(callback.BytesToWrite))
            {
                stream.Seek(callback.Offset, SeekOrigin.Begin);
                stream.Write(callback.Data, 0, callback.BytesToWrite);
                stream.Seek(0, SeekOrigin.Begin);

                LocalConfig.Current.Sentry = stream.ToArray();
            }

            LocalConfig.Current.SentryFileName = callback.FileName;
            LocalConfig.Save();

            using var sha = SHA1.Create();

            Steam.Instance.User.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
            {
                JobID = callback.JobID,

                FileName = callback.FileName,

                BytesWritten = callback.BytesToWrite,
                FileSize = LocalConfig.Current.Sentry.Length,
                Offset = callback.Offset,

                Result = EResult.OK,
                LastError = 0,

                OneTimePassword = callback.OneTimePassword,

                SentryFileHash = sha.ComputeHash(LocalConfig.Current.Sentry)
            });
        }

        private void OnLoginKey(SteamUser.LoginKeyCallback callback)
        {
            Log.WriteInfo(nameof(Steam), $"Got new login key with unique id {callback.UniqueID}");

            LocalConfig.Current.LoginKey = callback.LoginKey;
            LocalConfig.Save();

            Steam.Instance.User.AcceptNewLoginKey(callback);
        }
    }
}
