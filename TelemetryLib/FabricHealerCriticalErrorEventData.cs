﻿using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace FabricHealer.TelemetryLib
{
    public class FabricHealerCriticalErrorEventData
    {
        public string Version
        {
            get; set;
        }

        public string Source
        {
            get; set;
        }

        public string ErrorMessage
        {
            get; set;
        }

        public string ErrorStack
        {
            get; set;
        }

        public string CrashTime
        {
            get; set;
        }

        public string OS => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux";
    }
}
