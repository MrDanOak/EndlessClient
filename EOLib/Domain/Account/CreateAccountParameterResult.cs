﻿// Original Work Copyright (c) Ethan Moffat 2014-2016
// This file is subject to the GPL v2 License
// For additional details, see the LICENSE file

using System;
using EOLib.Localization;

namespace EOLib.Domain.Account
{
    public class CreateAccountParameterResult
    {
        public WhichParameter FaultingParameter { get; private set; }

        public DialogResourceID ErrorString { get; private set; }

        public CreateAccountParameterResult(WhichParameter faultingParameter, DialogResourceID errorString = DialogResourceID.NICE_TRY_HAXOR)
        {
            FaultingParameter = faultingParameter;
            ErrorString = errorString;
        }
    }

    [Flags]
    public enum WhichParameter
    {
        None,
        AccountName,
        Password,
        Confirm,
        RealName,
        Location,
        Email,
        All = AccountName | Password | Confirm | RealName | Location | Email
    }
}