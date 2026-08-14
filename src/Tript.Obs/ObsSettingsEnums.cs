// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

// enum obs_data_type [obs-data.h:46-53]. A settings entry carries exactly one of these, and
// changing it is what makes a wrongly-typed write destructive rather than merely ignored.
public enum ObsSettingsValueType
{
    Null = 0,
    String = 1,
    Number = 2,
    Boolean = 3,
    Object = 4,
    Array = 5
}

// enum obs_data_number_type [obs-data.h:55]. Only meaningful when the value type is Number:
// libobs stores integers and doubles in one slot and remembers which was written.
public enum ObsSettingsNumberType
{
    Invalid = 0,
    Integer = 1,
    Double = 2
}
