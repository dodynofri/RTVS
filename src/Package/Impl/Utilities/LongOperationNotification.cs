﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Microsoft.VisualStudio.R.Package.Shell;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.VisualStudio.R.Package.Utilities {
    internal sealed class LongAction {
        public string Name { get; set; }
        public Action<object> Action { get; set; }
        public object Data { get; set; }
    }

    internal static class LongOperationNotification {
        public static bool ShowWaitingPopup(string message, IReadOnlyList<LongAction> actions) {
            CommonMessagePump msgPump = new CommonMessagePump();
            msgPump.AllowCancel = true;
            msgPump.EnableRealProgress = true;
            msgPump.WaitTitle = "R Tools for Visual Studio";
            msgPump.WaitText = message;
            msgPump.TotalSteps = actions.Count;

            CancellationTokenSource cts = new CancellationTokenSource();
            Task task = Task.Run(() => {
                for (int i = 0; i < actions.Count; i++) {
                    cts.Token.ThrowIfCancellationRequested();
                    msgPump.CurrentStep = i + 1;
                    if (actions[i].Name == null) {
                        msgPump.ProgressText = string.Format(Resources.LongOperationProgressMessage1, i + 1, msgPump.TotalSteps);
                    } else {
                        msgPump.ProgressText = string.Format(Resources.LongOperationProgressMessage2, i + 1, msgPump.TotalSteps, actions[i].Name);
                    }
                    actions[i].Action(actions[i].Data);
                }
            }, cts.Token);

            var exitCode = msgPump.ModalWaitForHandles(((IAsyncResult)task).AsyncWaitHandle);

            if (exitCode == CommonMessagePumpExitCode.UserCanceled || exitCode == CommonMessagePumpExitCode.ApplicationExit) {
                cts.Cancel();
                msgPump = new CommonMessagePump();
                msgPump.AllowCancel = false;
                msgPump.EnableRealProgress = false;
                // Wait for the async operation to actually cancel.
                msgPump.ModalWaitForHandles(((IAsyncResult)task).AsyncWaitHandle);
            }

            if (task.IsCanceled) {
                return false;
            }
            task.Wait();
            return true;
        }
    }
}
