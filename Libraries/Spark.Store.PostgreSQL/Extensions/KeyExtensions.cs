/*
 * Copyright (c) 2026, Incendi <info@incendi.no>
 *
 * SPDX-License-Identifier: BSD-3-Clause
 */

using Spark.Engine.Core;
using System;

namespace Spark.Store.PostgreSQL.Extensions;

internal static class KeyExtensions
{
    /// <summary>
    /// A key can only be stored when it is internal (no base) and fully specified.
    /// </summary>
    public static void AssertKeyIsValid(this IKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        bool valid = key.Base == null && key.TypeName != null && key.ResourceId != null && key.VersionId != null;
        if (!valid)
        {
            throw new ArgumentException($"This key is not valid for storage: {key}", nameof(key));
        }
    }
}
