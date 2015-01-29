﻿using Hl7.Fhir.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Spark.Core
{
    public static class EntryHelper
    {
        public static Entry CreateDeletedEntry(this Key key)
        {
            Entry entry = new Entry(key, Method.Delete);
            return entry;
        }

        public static Entry CreateDeletedEntry(this Bundle.BundleEntryComponent bundleentry)
        {
            Key key = bundleentry.GetKey();
            return CreateDeletedEntry(key);
        }

        public static Entry CreateResourceEntry(this Bundle.BundleEntryComponent bundleEntry)
        {
            return new Entry(bundleEntry.Resource);
        }

        public static Entry CreateEntry(this Bundle.BundleEntryComponent bundleEntry)
        {
            if (bundleEntry.IsDeleted())
            {
                return CreateDeletedEntry(bundleEntry);
            }
            else
            {
                return CreateResourceEntry(bundleEntry);
            }
        }

        public static Bundle.BundleEntryComponent CreateBundleEntry(this Entry entry)
        {
            var bundleEntry = new Bundle.BundleEntryComponent();

            if (entry.Method == Method.Delete)
            {
                var deleted = new Bundle.BundleEntryDeletedComponent();
                deleted.ResourceId = entry.Key.ResourceId;
                deleted.VersionId = entry.Key.VersionId;
                deleted.Type = entry.Key.TypeName;
                bundleEntry.Deleted = deleted;
            }
            else
            {
                bundleEntry.Resource = entry.Resource;
            }
            return bundleEntry;
        }

        public static bool IsResource(this Bundle.BundleEntryComponent entry)
        {
            return (entry.Resource != null);
        }

        public static bool IsDeleted(this Bundle.BundleEntryComponent entry)
        {
            return (entry.Deleted != null);
        }

        public static IEnumerable<Resource> GetResources(this Bundle bundle)
        {
            return bundle.Entry.Where(e => e.IsResource()).Select(e => e.Resource);
        }

        public static Bundle Append(this Bundle bundle, IEnumerable<Entry> entries)
        {
            foreach (Entry entry in entries)
            {
                var bundleentry = entry.CreateBundleEntry();
                bundle.Entry.Add(bundleentry);
            }
            return bundle;
        }

        public static Bundle Replace(this Bundle bundle, IEnumerable<Entry> entries)
        {
            bundle.Entry = entries.Select(e => e.CreateBundleEntry()).ToList();
            return bundle;
        }

    }
}