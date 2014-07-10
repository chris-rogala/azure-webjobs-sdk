﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Dashboard.Data;
using Dashboard.HostMessaging;
using Dashboard.Indexers;
using Dashboard.ViewModels;
using Microsoft.Azure.Jobs.Host.Blobs;
using Microsoft.Azure.Jobs.Protocols;
using Microsoft.Azure.Jobs.Storage;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;

namespace Dashboard
{
    // $$$ Analysis can be expensive. Use a cache?
    public class LogAnalysis
    {
        internal static BlobBoundParamModel CreateExtendedBlobModel(FunctionInstanceSnapshot snapshot, FunctionInstanceArgument argument)
        {
            string[] components = argument.Value.Split(new char[] { '/' });

            if (components.Length != 2)
            {
                return null;
            }

            CloudBlockBlob blob = CloudStorageAccount.Parse(
                snapshot.StorageConnectionString).CreateCloudBlobClient().GetContainerReference(
                components[0]).GetBlockBlobReference(components[1]);

            var blobParam = new BlobBoundParamModel();
            blobParam.IsOutput = argument.IsBlobOutput;

            Guid? blobWriter = GetBlobWriter(blob);

            if (!blobWriter.HasValue)
            {
                blobParam.IsBlobMissing = true;
            }
            else
            {
                blobParam.OwnerId = blobWriter.Value;
                if (blobWriter.Value == snapshot.Id)
                {
                    blobParam.IsBlobOwnedByCurrentFunctionInstance = true;
                }
            }
            return blobParam;
        }

        /// <summary>
        /// Get the id of a function invocation that wrote a given blob.
        /// </summary>
        /// <returns>The function invocation's id, or Guid.Empty if no owner is specified, or null if the blob is missing.</returns>
        private static Guid? GetBlobWriter(ICloudBlob blob)
        {
            if (blob == null)
            {
                return null;
            }

            try
            {
                return BlobCausalityManager.GetWriter(blob) ?? Guid.Empty;
            }
            catch (StorageException e)
            {
                if (e.RequestInformation.HttpStatusCode == 404 || e.RequestInformation.HttpStatusCode == 400)
                {
                    // NoBlob
                    return null;
                }
                else
                {
                    throw;
                }
            }
        }

        // Get Live information from current watcher values. 
        internal static IDictionary<string, string> GetParameterLogs(FunctionInstanceSnapshot snapshot)
        {
            if (snapshot.ParameterLogs != null)
            {
                return ToStringDictionary(snapshot.ParameterLogs);
            }

            if (snapshot.ParameterLogBlob == null)
            {
                return null;
            }

            CloudBlockBlob blob = snapshot.ParameterLogBlob.GetBlockBlob(snapshot.StorageConnectionString);

            string contents;

            try
            {
                contents = blob.DownloadText();
            }
            catch (StorageException exception)
            {
                // common case, no status information written.
                if (exception.IsNotFound())
                {
                    return null;
                }
                else
                {
                    throw;
                }
            }

            IDictionary<string, ParameterLog> logs;

            try
            {
                logs = JsonConvert.DeserializeObject<IDictionary<string, ParameterLog>>(contents);
            }
            catch
            {
                // Not fatal. 
                // This could happen if the app wrote a corrupted log. 
                return null;
            }

            return ToStringDictionary(logs);
        }

        private static IDictionary<string, string> ToStringDictionary(IDictionary<string, ParameterLog> parameterLogs)
        {
            Dictionary<string, string> logs = new Dictionary<string, string>();

            foreach (KeyValuePair<string, ParameterLog> status in parameterLogs)
            {
                string value = Format(status.Value);

                if (value != null)
                {
                    logs.Add(status.Key, value);
                }
            }

            return logs;
        }

        private static string Format(ParameterLog log)
        {
            ReadBlobParameterLog readBlobLog = log as ReadBlobParameterLog;

            if (readBlobLog != null)
            {
                return Format(readBlobLog);
            }

            TableParameterLog tableLog = log as TableParameterLog;

            if (tableLog != null)
            {
                return Format(tableLog);
            }

            BinderParameterLog binderLog = log as BinderParameterLog;

            if (binderLog != null)
            {
                return Format(binderLog);
            }

            TextParameterLog textLog = log as TextParameterLog;

            if (textLog != null)
            {
                return textLog.Value;
            }

            return null;
        }

        private static string Format(ReadBlobParameterLog log)
        {
            Debug.Assert(log != null);
            StringBuilder builder = new StringBuilder();
            long bytesRead = log.BytesRead;
            double complete = bytesRead * 100.0 / log.Length;
            builder.AppendFormat("Read {0:n0} bytes ({1:0.00}% of total). ", bytesRead, complete);
            AppendNetworkTime(builder, log.ElapsedTime);
            return builder.ToString();
        }

        internal static void AppendNetworkTime(StringBuilder builder, TimeSpan elapsed)
        {
            if (elapsed == TimeSpan.Zero)
            {
                return;
            }

            builder.Append("(about ");

            string unitName;
            int unitCount;

            if (elapsed > TimeSpan.FromMinutes(55)) // it is about an hour, right?
            {
                unitName = "hour";
                unitCount = (int)Math.Round(elapsed.TotalHours);
            }
            else if (elapsed > TimeSpan.FromSeconds(55)) // it is about a minute, right?
            {
                unitName = "minute";
                unitCount = (int)Math.Round(elapsed.TotalMinutes);
            }
            else if (elapsed > TimeSpan.FromMilliseconds(950)) // it is about a second, right?
            {
                unitName = "second";
                unitCount = (int)Math.Round(elapsed.TotalSeconds);
            }
            else
            {
                unitName = "millisecond";
                unitCount = Math.Max((int)Math.Round(elapsed.TotalMilliseconds), 1);
            }
            builder.AppendFormat(CultureInfo.CurrentCulture, "{0} {1}{2}", unitCount, unitName, unitCount > 1 ? "s" : String.Empty);
            builder.Append(" spent on I/O)");
        }

        private static string Format(TableParameterLog log)
        {
            Debug.Assert(log != null);

            return String.Format(CultureInfo.CurrentCulture, "Updated {0} {1}", log.EntitiesUpdated,
                log.EntitiesUpdated == 1 ? "entity" : "entities");
        }

        private static string Format(BinderParameterLog log)
        {
            Debug.Assert(log != null);
            IEnumerable<BinderParameterLogItem> items = log.Items;

            if (items == null)
            {
                return null;
            }

            StringBuilder builder = new StringBuilder();
            builder.AppendFormat(CultureInfo.CurrentCulture, "Bound {0} object(s):", items.Count());
            builder.AppendLine();

            foreach (BinderParameterLogItem item in items)
            {
                ParameterSnapshot snapshot = HostIndexer.CreateParameterSnapshot(item.Descriptor);

                if (snapshot == null)
                {
                    continue;
                }

                builder.Append(snapshot.AttributeText);
                string status = Format(item.Log);

                if (status != null)
                {
                    builder.Append(" ");
                    builder.Append(status);
                }

                builder.AppendLine();
            }

            return builder.ToString();
        }
    }
}
