/*
 * -----------------------------------------------------------------
 * Copyright (c) 2012 Intel Corporation
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are
 * met:
 *
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *
 *     * Redistributions in binary form must reproduce the above
 *       copyright notice, this list of conditions and the following
 *       disclaimer in the documentation and/or other materials provided
 *       with the distribution.
 *
 *     * Neither the name of the Intel Corporation nor the names of its
 *       contributors may be used to endorse or promote products derived
 *       from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
 * "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
 * LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
 * A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE INTEL OR
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
 * EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
 * PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
 * PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
 * LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 *
 * EXPORT LAWS: THIS LICENSE ADDS NO RESTRICTIONS TO THE EXPORT LAWS OF
 * YOUR JURISDICTION. It is licensee's responsibility to comply with any
 * export regulations applicable in licensee's jurisdiction. Under
 * CURRENT (May 2000) U.S. export regulations this software is eligible
 * for export from the U.S. and can be downloaded by or otherwise
 * exported or reexported worldwide EXCEPT to U.S. embargoed destinations
 * which include Cuba, Iraq, Libya, North Korea, Iran, Syria, Sudan,
 * Afghanistan and any other country to which the U.S. has embargoed
 * goods and services.
 * -----------------------------------------------------------------
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Timers;

using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.CoreModules;
using OpenSim.Region.CoreModules.Framework.Statistics.Logging;

using OpenMetaverse.StructuredData;

using log4net;
using Nini.Config;

namespace DSG.RegionSync
{
// =================================================================================
public class SyncConnectorStat : CounterStat
{
    public int ConnectorNum { get; private set; }
    public string RegionName { get; private set; }
    public string MyActorID { get; private set; }
    public string OtherSideRegionName { get; private set; }
    public string OtherSideActorID { get; private set; }
    public string MessageType { get; private set; }

    public SyncConnectorStat(string shortName, string name, string description, string unitName, 
                            string pRegion, int pConnectorNum,
                            string pMyActorID, string pOtherSideRegionName, string pOtherSideActorID)
        : this(shortName, name, description, unitName, pRegion, pConnectorNum,
                                pMyActorID, pOtherSideRegionName, pOtherSideActorID, String.Empty)
    {
    }
    public SyncConnectorStat(string shortName, string name, string description, string unitName, 
                            string pRegion, int pConnectorNum,
                            string pMyActorID, string pOtherSideRegionName, string pOtherSideActorID, string pMessageType)
        : base(shortName, name, description, unitName, SyncStatisticCollector.DSGDetailCategory,
                                // The container name is the unique name for this syncConnector
                                pRegion + ".SyncConnector" + pConnectorNum.ToString(),
                                StatVerbosity.Debug)
    {
        RegionName = pRegion;
        ConnectorNum = pConnectorNum;
        MyActorID = pMyActorID;
        OtherSideActorID = pOtherSideActorID;
        OtherSideRegionName = pOtherSideRegionName;
        MessageType = pMessageType;
    }
}

// =================================================================================
public class SyncConnectorStatAggregator : Stat
{
    public SyncConnectorStatAggregator(string shortName, string name, string description, string unitName, string container)
                : base(shortName, name, description, unitName,
                        SyncStatisticCollector.DSGCategory, container, StatType.Push, null, StatVerbosity.Debug)
    {
    }

    public override string ToConsoleString()
    {
        return base.ToConsoleString();
    }

    // Build an OSDMap of the DSG sync connector info. Returned map is of the form:
    //   { regionName: {
    //           containerName: {
    //                "ConnectorNum": connectorNumber,
    //                "RegionName": reportingRegionName,
    //                "MyActorID": name,
    //                "OtherSideRegion": name,
    //                "OtherSideActor": name,
    //                "Bytes_Sent": num,
    //                "Bytes_Rcvd": num,
    //                "Msgs_Sent": num,
    //                "Msgs_Rcvd": num,
    //                "Queued_Msgs": num,
    //                "MessagesByType": {
    //                      typeName: {
    //                           "DSG_Msgs_Typ_Rcvd": num,
    //                           "DSG_Msgs_Typ_Sent": num,
    //                      },
    //                      ...
    //                },
    //                "Histograms": {
    //                    histogramName: [histogramValues],
    //                    ...
    //                },
    //           },
    //           ...
    //     },
    //     ...
    //   }
    public override OSDMap ToOSDMap()
    {
        OSDMap ret = new OSDMap();

        // Fetch all the DSG stats. Extract connectors and then organize the stats.
        // The top dictionary is the containers (region name)
        SortedDictionary<string, SortedDictionary<string, Stat>> DSGStats;
        if (StatsManager.TryGetStats(SyncStatisticCollector.DSGDetailCategory, out DSGStats))
        {
            foreach (string container in DSGStats.Keys)
            {
                OSDMap containerMap = new OSDMap();
                foreach (KeyValuePair<string, Stat> aStat in DSGStats[container])
                {
                    SyncConnectorStat connStat = aStat.Value as SyncConnectorStat;
                    if (connStat != null)
                    {
                        try
                        {
                            float val = (float)connStat.Value;
                            if (!containerMap.ContainsKey(container))
                            {
                                OSDMap connectorNew = new OSDMap();
                                connectorNew.Add("ConnectorNum", OSD.FromInteger(connStat.ConnectorNum));
                                connectorNew.Add("RegionName", connStat.RegionName);
                                connectorNew.Add("MyActorID", connStat.MyActorID);
                                connectorNew.Add("OtherSideActor", connStat.OtherSideActorID);
                                connectorNew.Add("OtherSideRegion", connStat.OtherSideRegionName);
                                connectorNew.Add("MessagesByType", new OSDMap());
                                connectorNew.Add("Histograms", new OSDMap());

                                containerMap.Add(container, connectorNew);
                            }
                            OSDMap connectorMap = (OSDMap)containerMap[container];
                            connectorMap.Add(connStat.Name, OSD.FromReal(val));
                            if (!string.IsNullOrEmpty(connStat.MessageType))
                            {
                                OSDMap messagesMap = (OSDMap)connectorMap["MessagesByType"];
                                if (!messagesMap.ContainsKey(connStat.MessageType))
                                {
                                    messagesMap.Add(connStat.MessageType, new OSDMap());
                                }
                                OSDMap messageMap = (OSDMap)messagesMap[connStat.MessageType];
                                messagesMap.Add(connStat.Name, OSD.FromReal(val));
                            }
                            OSDMap histogramMap = (OSDMap)connectorMap["Histograms"];
                            connStat.ForEachHistogram((histoName, histo) =>
                            {
                                histogramMap.Add(histoName, histo.GetHistogramAsOSDArray());
                            });
                        }
                        catch
                        {
                            // m_log.ErrorFormat("{0} Exception adding stat to block. name={1}, block={2}, e={3}",
                            //             LogHeader, aStat.Value.ShortName, OSDParser.SerializeJsonString(containerMap), e);
                        }
                    }
                }
                ret.Add(container, containerMap);
            }
        }
        return ret;
    }
}

// =================================================================================
public class SyncStatisticCollector : IDisposable
{
    private readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private readonly string LogHeader = "[DSG SYNC STATISTICS COLLECTOR]";

    public bool Enabled { get; set; }
    public static string DSGCategory = "dsg";
    public static string DSGDetailCategory = "dsg-detail";

    private int LogIntervalSeconds { get; set; }
    private System.Timers.Timer WriteTimer { get; set; }

    private bool LogSyncConnectorEnable { get; set; }
    private string LogSyncConnectorDirectory { get; set; }
    private string LogSyncConnectorFilenamePrefix { get; set; }
    private bool LogSyncConnectorIncludeTitleLine { get; set; }
    private int LogSyncConnectorFileTimeMinutes { get; set; }
    private bool LogSyncConnectorFlushWrites { get; set; }

    public SyncStatisticCollector(IConfig cfg)
    {
        Enabled = cfg.GetBoolean("StatisticLoggingEnable", false);
        if (Enabled)
        {
            DSGCategory = cfg.GetString("LogDSGCategory", "dsg");
            DSGCategory = cfg.GetString("LogDSGDetailCategory", "dsg-detail");
            LogIntervalSeconds = cfg.GetInt("LogIntervalSeconds", 10);
            m_log.InfoFormat("{0} Enabling statistic logging. Category={1}, Interval={2}sec",
                        LogHeader, DSGCategory, LogIntervalSeconds);

            LogSyncConnectorEnable = cfg.GetBoolean("LogSyncConnectorEnable", false);
            if (LogSyncConnectorEnable)
            {
                LogSyncConnectorDirectory = cfg.GetString("LogSyncConnectorDirectory", ".");
                LogSyncConnectorFilenamePrefix = cfg.GetString("LogSyncConnectorFilenamePrefix", "conn-%CONTAINER%-%THISACTOR%-%OTHERSIDEACTOR%-");
                LogSyncConnectorIncludeTitleLine = cfg.GetBoolean("LogSyncConnectorIncludeTitleLine", true);
                LogSyncConnectorFileTimeMinutes = cfg.GetInt("LogSyncConnectorFileTimeMinutes", 10);
                LogSyncConnectorFlushWrites = cfg.GetBoolean("LogSyncConnectorFlushWrites", false);

                m_log.InfoFormat("{0} Enabling SyncConnector logging. Dir={1}, fileAge={2}min, flush={3}",
                        LogHeader, LogSyncConnectorDirectory, LogSyncConnectorFileTimeMinutes, LogSyncConnectorFlushWrites);
            }

            WriteTimer = new Timer(LogIntervalSeconds * 1000);
            WriteTimer.Elapsed += StatsTimerElapsed;
            WriteTimer.Start();
        }
    }

    private void StatsTimerElapsed(object source, System.Timers.ElapsedEventArgs e)
    {
        if (!Enabled)
        {
            WriteTimer.Stop();
            return;
        }
        
        if (LogSyncConnectorEnable)
            LogConnectorStats();
    }

    public void Dispose()
    {
        Enabled = false;
        LogSyncConnectorEnable = false;
        if (WriteTimer != null)
        {
            WriteTimer.Stop();
            WriteTimer.Dispose();
            WriteTimer = null;
        }
        Close();
    }

    // Close out all the connected log file writers.
    // If this is done while the system is running, they will get reopened.
    public void Close()
    {
        if (ConnectionLoggers != null)
        {
            foreach (LogWriter connWriter in ConnectionLoggers.Values)
            {
                connWriter.Close();
            }
            ConnectionLoggers.Clear();
        }
    }

    private Dictionary<string, LogWriter> ConnectionLoggers = new Dictionary<string, LogWriter>();
    private void LogConnectorStats()
    {
        SortedDictionary<string, SortedDictionary<string, Stat>> DSGStats;
        if (StatsManager.TryGetStats(DSGDetailCategory, out DSGStats))
        {
            foreach (string container in DSGStats.Keys)
            {
                SortedDictionary<string, Stat> containerStats = DSGStats[container];
                LogWriter connWriter = null;
                Dictionary<string, string> outputValues = new Dictionary<string, string>();
                SyncConnectorStat lastStat = null;

                foreach (Stat aStat in containerStats.Values)
                {
                    // Select out only the SyncConnector stats.
                    SyncConnectorStat connStat = aStat as SyncConnectorStat;
                    if (connStat != null)
                    {
                        lastStat = connStat;    // remember one of the stats for line output info

                        // Get the log file writer for this connection and create one if necessary.
                        if (connWriter == null)
                        {
                            if (!ConnectionLoggers.TryGetValue(container, out connWriter))
                            {
                                string headr = LogSyncConnectorFilenamePrefix;
                                headr = headr.Replace("%CONTAINER%", container);
                                headr = headr.Replace("%REGIONNAME%", connStat.RegionName);
                                headr = headr.Replace("%CONNECTIONNUMBER%", connStat.ConnectorNum.ToString());
                                headr = headr.Replace("%THISACTOR%", connStat.MyActorID);
                                headr = headr.Replace("%OTHERSIDEACTOR%", connStat.OtherSideActorID);
                                headr = headr.Replace("%OTHERSIDEREGION%", connStat.OtherSideRegionName);
                                headr = headr.Replace("%MESSAGETYPE%", connStat.MessageType);
                                connWriter = new LogWriter(LogSyncConnectorDirectory, headr, LogSyncConnectorFileTimeMinutes, LogSyncConnectorFlushWrites);
                                ConnectionLoggers.Add(container, connWriter);

                                if (LogSyncConnectorIncludeTitleLine)
                                {
                                    StringBuilder bufft = new StringBuilder();
                                    bufft.Append("Region");
                                    bufft.Append(",");
                                    bufft.Append("SyncConnNum");
                                    bufft.Append(",");
                                    bufft.Append("ActorID");
                                    bufft.Append(",");
                                    bufft.Append("OtherSideActorID");
                                    bufft.Append(",");
                                    bufft.Append("OtherSideRegionName");
                                    bufft.Append(",");
                                    bufft.Append("Msgs_Sent");
                                    bufft.Append(",");
                                    bufft.Append("Msgs_Rcvd");
                                    bufft.Append(",");
                                    bufft.Append("Bytes_Sent");
                                    bufft.Append(",");
                                    bufft.Append("Bytes_Rcvd");
                                    bufft.Append(",");
                                    bufft.Append("Queued_Msgs");
                                    bufft.Append(",");
                                    bufft.Append("UpdatedProperties_Sent");
                                    bufft.Append(",");
                                    bufft.Append("UpdatedProperties_Rcvd");
                                    bufft.Append(",");
                                    bufft.Append("NewObject_Sent");
                                    bufft.Append(",");
                                    bufft.Append("NewObject_Rcvd");
                                    bufft.Append(",");
                                    bufft.Append("NewPresence_Sent");
                                    bufft.Append(",");
                                    bufft.Append("NewPresence_Rcvd");
                                    connWriter.Write(bufft.ToString());
                                }
                            }
                        }
                    }
                    outputValues.Add(connStat.Name, connStat.Value.ToString());
                }

                if (lastStat != null)
                {
                    StringBuilder buff = new StringBuilder();
                    buff.Append(lastStat.RegionName);
                    buff.Append(",");
                    buff.Append(lastStat.ConnectorNum.ToString());
                    buff.Append(",");
                    buff.Append(lastStat.MyActorID);
                    buff.Append(",");
                    buff.Append(lastStat.OtherSideActorID);
                    buff.Append(",");
                    buff.Append(lastStat.OtherSideRegionName);
                    buff.Append(",");
                    buff.Append(outputValues.ContainsKey("Msgs_Sent") ? outputValues["Msgs_Sent"] : "");
                    buff.Append(",");
                    buff.Append(outputValues.ContainsKey("Msgs_Rcvd") ? outputValues["Msgs_Rcvd"] : "");
                    buff.Append(",");
                    buff.Append(outputValues.ContainsKey("Bytes_Sent") ? outputValues["Bytes_Sent"] : "");
                    buff.Append(",");
                    buff.Append(outputValues.ContainsKey("Bytes_Rcvd") ? outputValues["Bytes_Rcvd"] : "");
                    buff.Append(",");
                    buff.Append(outputValues.ContainsKey("Queued_Msgs") ? outputValues["Queued_Msgs"] : "");
                    buff.Append(",");
                    buff.Append(outputValues.ContainsKey("UpdatedProperties_Sent") ? outputValues["UpdatedProperties_Sent"] : "");
                    buff.Append(",");
                    buff.Append(outputValues.ContainsKey("UpdatedProperties_Rcvd") ? outputValues["UpdatedProperties_Rcvd"] : "");
                    buff.Append(",");
                    buff.Append(outputValues.ContainsKey("NewObject_Sent") ? outputValues["NewObject_Sent"] : "");
                    buff.Append(",");
                    buff.Append(outputValues.ContainsKey("NewObject_Rcvd") ? outputValues["NewObject_Rcvd"] : "");
                    buff.Append(",");
                    buff.Append(outputValues.ContainsKey("NewPresence_Sent") ? outputValues["NewPresence_Sent"] : "");
                    buff.Append(",");
                    buff.Append(outputValues.ContainsKey("NewPresence_Rcvd") ? outputValues["NewPresence_Rcvd"] : "");

                    // buff.Append(outputValues.ContainsKey("NewScript_Sent") ? outputValues["NewScript_Sent"] : "");
                    // buff.Append(outputValues.ContainsKey("NewScript_Rcvd") ? outputValues["NewScript_Rcvd"] : "");
                    // buff.Append(outputValues.ContainsKey("UpdateScript_Sent") ? outputValues["UpdateScript_Sent"] : "");
                    // buff.Append(outputValues.ContainsKey("UpdateScript_Rcvd") ? outputValues["UpdateScript_Rcvd"] : "");
                    // buff.Append(outputValues.ContainsKey("Terrain") ? outputValues["Terrain"] : "");
                    // buff.Append(outputValues.ContainsKey("GetObjects") ? outputValues["GetObjects"] : "");
                    // buff.Append(outputValues.ContainsKey("GetPresences") ? outputValues["GetPresences"] : "");
                    // buff.Append(outputValues.ContainsKey("GetTerrain") ? outputValues["GetTerrain"] : "");
                    connWriter.Write(buff.ToString());
                }
            }
        }
    }
}
}
