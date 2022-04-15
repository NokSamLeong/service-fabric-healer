﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Runtime.Serialization;

namespace FabricHealerLib
{
    /// <summary>
    /// Exception thrown when RepairData instance is missing values for required non-null members (E.g., NodeName).
    /// </summary>
    [Serializable]
    public class MissingRequiredDataException : Exception
    {
        /// <summary>
        /// Creates an instance of MissingRequiredDataException.
        /// </summary>
        public MissingRequiredDataException()
        {
        }

        /// <summary>
        ///  Creates an instance of MissingRequiredDataException.
        /// </summary>
        /// <param name="message">Error message that describes the problem.</param>
        public MissingRequiredDataException(string message) : base(message)
        {
        }

        /// <summary>
        /// Creates an instance of MissingRequiredDataException.
        /// </summary>
        /// <param name="message">Error message that describes the problem.</param>
        /// <param name="innerException">InnerException instance.</param>
        public MissingRequiredDataException(string message, Exception innerException) : base(message, innerException)
        {
        }

        /// <summary>
        /// Creates an instance of MissingRequiredDataException.
        /// </summary>
        /// <param name="info">SerializationInfo</param>
        /// <param name="context">StreamingContext</param>
        protected MissingRequiredDataException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}