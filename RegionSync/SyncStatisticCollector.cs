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
using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Timers;

using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Monitoring;
using OpenSim.Framework.Servers;
using OpenSim.Region.CoreModules;
using OpenSim.Region.CoreModules.Framework.Statistics.Logging;
using OpenSim.Region.Framework.Scenes;

using OpenMetaverse.StructuredData;

using log4net;
using Nini.Config;

using OpenSim.Region.ClientStack.LindenUDP; 

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

        public override string ToConsoleString()
        {
            return base.ToConsoleString();
        }

        public override OSDMap ToOSDMap()
        {
            // Start with the underlying value map
            OSDMap map = base.ToOSDMap();

            map["StatType"] = "SyncConnectorStat";
            map.Add("RegionName", RegionName);
            map.Add("ConnectorNum", ConnectorNum);
            map.Add("MyActorID", MyActorID);
            map.Add("OtherSideActorID", OtherSideActorID);
            map.Add("OtherSideRegionName", OtherSideRegionName);
            map.Add("MessageType", MessageType);

            return map;
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
            // Message types that are not reported. There should be only one of these messages anyway.
            string leaveOutMsgs = "GetObjec,GetPrese,GetTerra,Terrain,GetRegio,RegionIn,";
            // Remembers the name of the actor on the other side of a connector
            Dictionary<string, string> otherActor = new Dictionary<string, string>();

            ConsoleDisplayTable cdt = new ConsoleDisplayTable();

            SortedDictionary<string, SortedDictionary<string, Stat>> DSGStats;
            if (StatsManager.TryGetStats(SyncStatisticCollector.DSGDetailCategory, out DSGStats))
            {
                // Find all the column names
                Dictionary<string, int> cols = new Dictionary<string, int>();
                int maxColumn = 2;  // two extra columns at beginning
                foreach (string container in DSGStats.Keys)
                {
                    foreach (KeyValuePair<string, Stat> aStat in DSGStats[container])
                    {
                        string colName = ExtractColumnNameFromStatShortname(aStat.Value.ShortName);
                        if (!leaveOutMsgs.Contains(colName + ",") && !cols.ContainsKey(colName))
                        {
                            cols.Add(colName, maxColumn++);
                        }
                        if (!otherActor.ContainsKey(container))
                        {
                            SyncConnectorStat conStat = aStat.Value as SyncConnectorStat;
                            if (conStat != null)
                                otherActor[container] = conStat.OtherSideActorID;
                        }
                    }
                }

                // Add all the columns to the table
                cdt.AddColumn("Connection", 22);
                cdt.AddColumn("OtherActor", 10);
                foreach (KeyValuePair<string, int> kvp in cols)
                {
                    cdt.AddColumn(kvp.Key, 8);
                }

                // Add a row for each of the containers
                foreach (string container in DSGStats.Keys)
                {
                    string[] values = new string[maxColumn];
                    values.Initialize();

                    values[0] = container;
                    if (otherActor.ContainsKey(container))
                        values[1] = otherActor[container];

                    foreach (KeyValuePair<string, Stat> aStat in DSGStats[container])
                    {
                        string colName = ExtractColumnNameFromStatShortname(aStat.Value.ShortName);
                        if (cols.ContainsKey(colName))
                            values[cols[colName]] = aStat.Value.Value.ToString();
                    }

                    cdt.AddRow(values);
                }
            }
            return cdt.ToString();
        }

        private string ExtractColumnNameFromStatShortname(string shortName)
        {
            string ret = shortName;

            // The shortname is like:
            //   DSG_Queued_Msgs|SyncConnector0(script/rsea100)
            //   DSG_Msgs_Typ_Rcvd|SyncConnector1(physics/rpea00)|GetObjects
            // This code converts the first into "Queued_Msgs" and the second into "GetObjects".
            string[] barPieces = ret.Split('|');
            if (barPieces.Length == 2)
            {
                ret = barPieces[0];
                ret = ret.Replace("DSG_Bytes_", "Byte");
                ret = ret.Replace("DSG_Msgs_", "Msg");
                ret = ret.Replace("DSG_", "");
            }
            if (barPieces.Length == 3)
            {
                if (barPieces[0].StartsWith("DSG_Msgs_Typ"))
                {
                    ret = barPieces[2];
                }
            }
            if (ret.Length > 8)
                ret = ret.Substring(0, 8);

            return ret;
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
        //                    histogramName: {
        //                         "Buckets": numberOfBuckets,
        //                         "BucketMilliseconds": millisecondsOfEachBucket,
        //                         "TotalMilliseconds": totalMillisecondsSpannedByHistogram,
        //                         "BaseNumber": numberOfFirstBucket,
        //                         "Values": [ arrayOfBucketValues ]
        //                    }
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
                                // If the first entry for this container, initialize the container block
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
                                // Get the structure being built for this container
                                OSDMap connectorMap = (OSDMap)containerMap[container];

                                // Add this statistic value
                                float val = (float)connStat.Value;
                                connectorMap.Add(connStat.Name, OSD.FromReal(val));

                                // If this value is a message type entry, add the info to the by message type table
                                if (!string.IsNullOrEmpty(connStat.MessageType))
                                {
                                    OSDMap messagesMap = (OSDMap)connectorMap["MessagesByType"];
                                    messagesMap.Add(connStat.Name, OSD.FromReal(val));
                                }

                                // If there are histograms on the statistics, add them to the structure
                                OSDMap histogramMap = (OSDMap)connectorMap["Histograms"];
                                connStat.ForEachHistogram((histoName, histo) =>
                                {
                                    histogramMap.Add(histoName, histo.GetHistogramAsOSDMap());
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

        private string RegionName = "pre";
        private Scene AssociatedScene { get; set; }

        private int LogIntervalSeconds { get; set; }
        private System.Timers.Timer WriteTimer { get; set; }

        private bool LogSyncConnectorEnable { get; set; }
        private string LogSyncConnectorDirectory { get; set; }
        private string LogSyncConnectorFilenamePrefix { get; set; }
        private bool LogSyncConnectorIncludeTitleLine { get; set; }
        private int LogSyncConnectorFileTimeMinutes { get; set; }
        private bool LogSyncConnectorFlushWrites { get; set; }

        private bool LogRegionEnable { get; set; }
        private string LogRegionDirectory { get; set; }
        private string LogRegionFilenamePrefix { get; set; }
        private bool LogRegionIncludeTitleLine { get; set; }
        private int LogRegionFileTimeMinutes { get; set; }
        private bool LogRegionFlushWrites { get; set; }

        private bool LogServerEnable { get; set; }
        private string LogServerDirectory { get; set; }
        private string LogServerFilenamePrefix { get; set; }
        private bool LogServerIncludeTitleLine { get; set; }
        private int LogServerFileTimeMinutes { get; set; }
        private bool LogServerFlushWrites { get; set; }

        private bool LogLLUDPBWAggEnabled { get; set; }
        private string LogLLUDPBWAggDirectory { get; set; }
        private string LogLLUDPBWAggFilenamePrefix { get; set; }
        private bool LogLLUDPBWAggIncludeTitleLine { get; set; }
        private int LogLLUDPBWAggFileTimeMinutes { get; set; }
        private bool LogLLUDPBWAggFlushWrites { get; set; }
        private int LogLLUDPBWAggInterval { get; set; }

        private bool RemoteStatsFetchEnabled { get; set; }
        private string RemoteStatsFetchBase { get; set; }

        public SyncStatisticCollector(IConfig cfg)
        {
            Enabled = cfg.GetBoolean("StatisticLoggingEnable", false);
            if (Enabled)
            {
                DSGCategory = cfg.GetString("LogDSGCategory", "dsg");
                DSGDetailCategory = cfg.GetString("LogDSGDetailCategory", "dsg-detail");
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

                LogRegionEnable = cfg.GetBoolean("LogRegionEnable", false);
                if (LogRegionEnable)
                {
                    LogRegionDirectory = cfg.GetString("LogRegionDirectory", ".");
                    LogRegionFilenamePrefix = cfg.GetString("LogRegionFilenamePrefix", "%CATEGORY%-%CONTAINER%-%REGIONNAME%-");
                    LogRegionIncludeTitleLine = cfg.GetBoolean("LogRegionIncludeTitleLine", true);
                    LogRegionFileTimeMinutes = cfg.GetInt("LogRegionFileTimeMinutes", 10);
                    LogRegionFlushWrites = cfg.GetBoolean("LogRegionFlushWrites", false);

                    m_log.InfoFormat("{0} Enabling region logging. Dir={1}, fileAge={2}min, flush={3}",
                            LogHeader, LogRegionDirectory, LogRegionFileTimeMinutes, LogRegionFlushWrites);
                }

                LogServerEnable = cfg.GetBoolean("LogServerEnable", false);
                if (LogServerEnable)
                {
                    LogServerDirectory = cfg.GetString("LogServerDirectory", ".");
                    LogServerFilenamePrefix = cfg.GetString("LogServerFilenamePrefix", "%CATEGORY%-%REGIONNAME%-");
                    LogServerIncludeTitleLine = cfg.GetBoolean("LogServerIncludeTitleLine", true);
                    LogServerFileTimeMinutes = cfg.GetInt("LogServerFileTimeMinutes", 10);
                    LogServerFlushWrites = cfg.GetBoolean("LogServerFlushWrites", false);

                    m_log.InfoFormat("{0} Enabling server logging. Dir={1}, fileAge={2}min, flush={3}",
                            LogHeader, LogServerDirectory, LogServerFileTimeMinutes, LogServerFlushWrites);
                }

                LogLLUDPBWAggEnabled = cfg.GetBoolean("LogLLUDPBWAggEnabled", false);
                if (LogLLUDPBWAggEnabled)
                {
                    LogLLUDPBWAggDirectory = cfg.GetString("LogLLUDPBWAggDirectory", ".");
                    LogLLUDPBWAggFilenamePrefix = cfg.GetString("LogLLUDPBWAggFilenamePrefix", "%REGIONNAME%-LLUDPAggBytes");
                    LogLLUDPBWAggIncludeTitleLine = cfg.GetBoolean("LogLLUDPBWAggIncludeTitleLine", true);
                    LogLLUDPBWAggFileTimeMinutes = cfg.GetInt("LogLLUDPBWAggFileTimeMinutes", 10);
                    LogLLUDPBWAggFlushWrites = cfg.GetBoolean("LogLLUDPBWAggFlushWrites", false);
                    LogLLUDPBWAggInterval = cfg.GetInt("LogLLUDPBWAggInterval",10); //default: caldulate every 10 seconds, configured in unit of seconds

                    m_log.InfoFormat("{0} Enabling server logging. Dir={1}, fileAge={2}min, flush={3}",
                            LogHeader, LogLLUDPBWAggDirectory, LogLLUDPBWAggFileTimeMinutes, LogLLUDPBWAggFlushWrites);
                }

                RemoteStatsFetchEnabled = cfg.GetBoolean("RemoteStatsFetchEnabled", false);
                if (RemoteStatsFetchEnabled)
                {
                    RemoteStatsFetchBase = cfg.GetString("RemoteStatsFetchbase", "DSGStats");
                    // That was simple
                }

                SetupRemoteStatsFetch();

                // If enabled, we add a DSG pretty printer to the output
                StatsManager.RegisterStat(new SyncConnectorStatAggregator(DSGCategory, DSGCategory, "Distributed Scene Graph", "", DSGCategory));
            }
            lastStatTime = Util.EnvironmentTickCount();
            m_lastLLUDPStatsLogTime = Util.EnvironmentTickCount(); //Util.EnvironmentTickCount() 15.6ms precision
        }

        public void SpecifyRegion(Scene pScene, string pRegionName)
        {
            AssociatedScene = pScene;
            RegionName = pRegionName;

            // Once the region is set, we can start gathering statistics
            if (Enabled)
            {
                RegisterDSGSpecificStats();

                WriteTimer = new Timer(LogIntervalSeconds * 1000);
                WriteTimer.Elapsed += StatsTimerElapsed;
                WriteTimer.Start();
            }
        }

        List<string> regionStatFields = new List<string>
        {       "RootAgents", "ChildAgents", "SimFPS", "PhysicsFPS", "TotalPrims",
                "ActivePrims", "ActiveScripts", "ScriptLines",
                "FrameTime", "PhysicsTime", "AgentTime", "ImageTime", "NetTime", "OtherTime", "SimSpareMS",
                "AgentUpdates", "SlowFrames", "TimeDilation", "RealAgents",
        };
        List<string> serverStatFields = new List<string>
        {
                "CPUPercent", "TotalProcessorTime", "UserProcessorTime", "PrivilegedProcessorTime",
                "Threads",
                "AverageMemoryChurn", "LastMemoryChurn",
                "ObjectMemory", "ProcessMemory",
                // "BytesRcvd", "BytesSent", "TotalBytes",
        };

        private void StatsTimerElapsed(object source, System.Timers.ElapsedEventArgs e)
        {
            if (!Enabled)
            {
                WriteTimer.Stop();
                return;
            }

            if (LogSyncConnectorEnable)
                LogConnectorStats();
            if (LogRegionEnable)
                LogStats("scene", LogRegionDirectory, LogRegionFilenamePrefix, LogRegionFileTimeMinutes, LogRegionFlushWrites, regionStatFields);
            if (LogServerEnable)
                LogStatsCombineCategory("server", LogServerDirectory, LogServerFilenamePrefix, LogServerFileTimeMinutes, LogServerFlushWrites, serverStatFields);

            if (LogLLUDPBWAggEnabled)
                LogLLUDPStats();
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
            if (StatLoggers != null)
            {
                foreach (LogWriter connWriter in StatLoggers.Values)
                {
                    connWriter.Close();
                }
                StatLoggers.Clear();
            }

            if (m_LLUDPStatsLogWriter != null)
                m_LLUDPStatsLogWriter.Close();
        }

        private void RegisterDSGSpecificStats()
        {
            // The number of real clients in a region. Counts the number of ScenePresences actually connected to a viewer.
            StatsManager.RegisterStat(new Stat("RealAgents", "RealAgents", "RealAgents", "avatars", "scene", RegionName, StatType.Pull,
                (s) => { 
                    int realAgents = 0;
                    AssociatedScene.ForEachScenePresence((sp) => { if (sp.ControllingClient is LLClientView) realAgents++; });
                    s.Value = realAgents;
                }, StatVerbosity.Info));
        }

        int lastStatTime = 0;
        // Structure kept per connection to remember last values so we can do per-second calculations
        private class LastStatValues
        {
            public LastStatValues()
            {
                lastMsgs_Rcvd = lastMsgs_Sent = lastBytes_Rcvd = lastBytes_Sent = 0d;
            }
            public double lastMsgs_Sent;
            public double lastMsgs_Rcvd;
            public double lastBytes_Sent;
            public double lastBytes_Rcvd;
        }
        private Dictionary<string, LastStatValues> m_lastStatValues = new Dictionary<string, LastStatValues>();

        private Dictionary<string, LogWriter> ConnectionLoggers = new Dictionary<string, LogWriter>();
        private void LogConnectorStats()
        {
            List<string> fields = new List<string>
                { "Msgs_Sent", "Msgs_Rcvd", "Bytes_Sent", "Bytes_Rcvd",
                  "Msgs_Sent_Per_Sec", "Msgs_Rcvd_Per_Sec", "Bytes_Sent_Per_Sec", "Bytes_Rcvd_Per_Sec",
                 "Queued_Msgs",
                 "UpdatedProperties_Sent", "UpdatedProperties_Rcvd",
                 "NewObject_Sent", "NewObject_Rcvd", "NewPresence_Sent", "NewPresence_Rcvd"
                };

            // Milliseconds since the last time we collected statistics
            int msSinceLast = Util.EnvironmentTickCountSubtract(lastStatTime);

            SortedDictionary<string, SortedDictionary<string, Stat>> DSGStats;
            if (StatsManager.TryGetStats(DSGDetailCategory, out DSGStats))
            {
                foreach (string container in DSGStats.Keys)
                {
                    LogWriter connWriter = null;
                    SyncConnectorStat lastStat = null;
                    Dictionary<string, double> outputValues = new Dictionary<string, double>();

                    SortedDictionary<string, Stat> containerStats = DSGStats[container];
                    foreach (Stat aStat in containerStats.Values)
                    {
                        // Select out only the SyncConnector stats.
                        SyncConnectorStat connStat = aStat as SyncConnectorStat;
                        if (connStat != null)
                        {
                            lastStat = connStat;    // remember one of the stats for line output info
                            outputValues.Add(connStat.Name, connStat.Value);
                        }
                    }

                    // Get the log file writer for this connection and create one if necessary.
                    if (lastStat != null)
                    {
                        if (!ConnectionLoggers.TryGetValue(container, out connWriter))
                        {
                            string headr = LogSyncConnectorFilenamePrefix;
                            headr = headr.Replace("%CONTAINER%", container);
                            headr = headr.Replace("%REGIONNAME%", lastStat.RegionName);
                            headr = headr.Replace("%CONNECTIONNUMBER%", lastStat.ConnectorNum.ToString());
                            headr = headr.Replace("%THISACTOR%", lastStat.MyActorID);
                            headr = headr.Replace("%OTHERSIDEACTOR%", lastStat.OtherSideActorID);
                            headr = headr.Replace("%OTHERSIDEREGION%", lastStat.OtherSideRegionName);
                            headr = headr.Replace("%MESSAGETYPE%", lastStat.MessageType);
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
                                foreach (string fld in fields)
                                {
                                    bufft.Append(",");
                                    bufft.Append(fld);
                                }

                                connWriter.Write(bufft.ToString());
                            }
                        }

                        LastStatValues lastValues;
                        if (!m_lastStatValues.TryGetValue(container, out lastValues))
                        {
                            lastValues = new LastStatValues();
                            m_lastStatValues.Add(container, lastValues);

                        }
                        // Compute some useful values
                        ComputePerSecond("Msgs_Sent", "Msgs_Sent_Per_Sec", ref outputValues, ref lastValues.lastMsgs_Sent, msSinceLast);
                        ComputePerSecond("Msgs_Rcvd", "Msgs_Rcvd_Per_Sec", ref outputValues, ref lastValues.lastMsgs_Rcvd, msSinceLast);
                        ComputePerSecond("Bytes_Sent", "Bytes_Sent_Per_Sec", ref outputValues, ref lastValues.lastBytes_Sent, msSinceLast);
                        ComputePerSecond("Bytes_Rcvd", "Bytes_Rcvd_Per_Sec", ref outputValues, ref lastValues.lastBytes_Rcvd, msSinceLast);

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
                        foreach (string fld in fields)
                        {
                            buff.Append(",");
                            buff.Append(outputValues.ContainsKey(fld) ? outputValues[fld].ToString() : "");
                        }

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
            lastStatTime = Util.EnvironmentTickCount();
        }

        // Compute an avarea per second givne the current values and pointers to the previous values and time since previous sample.
        // This updates pOutputValues with the per second value.
        // Also updates the previous value.
        private void ComputePerSecond(string pAccumName, string pPerSecName, ref Dictionary<string, double> pOutputValues, ref double pPrevValue, int msSinceLast)
        {
            if (pOutputValues.ContainsKey(pAccumName))
            {
                double perSec = 0;
                double currentVal = pOutputValues[pAccumName];
                if (msSinceLast > 50)
                {
                    perSec = (currentVal - pPrevValue) / ((double)msSinceLast / 1000d);
                    pOutputValues.Add(pPerSecName, perSec);
                }
                pPrevValue = currentVal;
            }
        }

        private Dictionary<string, LogWriter> StatLoggers = new Dictionary<string, LogWriter>();
        private void LogStats(string category, string logDir, string logPrefix, int logFileTime, bool logFlushWrites, List<string> fields)
        {
            SortedDictionary<string, SortedDictionary<string, Stat>> categoryStats;
            if (StatsManager.TryGetStats(category, out categoryStats))
            {
                foreach (string container in categoryStats.Keys)
                {
                    SortedDictionary<string, Stat> containerStats = categoryStats[container];
                    LogWriter connWriter = null;
                    Dictionary<string, string> outputValues = new Dictionary<string, string>();

                    foreach (Stat rStat in containerStats.Values)
                    {
                        // Get the log file writer for this connection and create one if necessary.
                        if (connWriter == null)
                        {
                            string loggerName = category + "/" + container;
                            if (!StatLoggers.TryGetValue(loggerName, out connWriter))
                            {
                                string headr = logPrefix;
                                headr = headr.Replace("%CATEGORY%", category);
                                headr = headr.Replace("%CONTAINER%", container);
                                headr = headr.Replace("%REGIONNAME%", RegionName);
                                connWriter = new LogWriter(logDir, headr, logFileTime, logFlushWrites);
                                StatLoggers.Add(loggerName, connWriter);

                                if (LogRegionIncludeTitleLine)
                                {
                                    StringBuilder bufft = new StringBuilder();
                                    bufft.Append("Category");
                                    bufft.Append(",");
                                    bufft.Append("Container");
                                    foreach (string fld in fields)
                                    {
                                        bufft.Append(",");
                                        bufft.Append(fld);
                                    }

                                    connWriter.Write(bufft.ToString());
                                }
                            }
                        }
                        outputValues.Add(rStat.Name, rStat.Value.ToString());
                    }

                    StringBuilder buff = new StringBuilder();
                    buff.Append(category);
                    buff.Append(",");
                    buff.Append(container);
                    foreach (string fld in fields)
                    {
                        buff.Append(",");
                        buff.Append(outputValues.ContainsKey(fld) ? outputValues[fld] : "");
                    }
                    connWriter.Write(buff.ToString());
                }
            }
        }

        // There may be multiple containers but combine all containers under the one category.
        private void LogStatsCombineCategory(string category, string logDir, string logPrefix, int logFileTime, bool logFlushWrites, List<string> fields)
        {
            SortedDictionary<string, SortedDictionary<string, Stat>> categoryStats;
            LogWriter connWriter = null;
            Dictionary<string, string> outputValues = new Dictionary<string, string>();
            if (StatsManager.TryGetStats(category, out categoryStats))
            {
                foreach (string container in categoryStats.Keys)
                {
                    SortedDictionary<string, Stat> containerStats = categoryStats[container];
                    if (container == "network")
                    {
                        // Super special kludge that adds the field headers for the network stats.
                        // This is done this way because the name includes the name of the NIC
                        //     and there could be more than one NIC.
                        foreach (Stat rStat in containerStats.Values)
                        {
                            if (!serverStatFields.Contains(rStat.Name))
                            {
                                serverStatFields.Add(rStat.Name);
                            }
                        }
                    }
                    foreach (Stat rStat in containerStats.Values)
                    {
                        outputValues.Add(rStat.Name, rStat.Value.ToString());
                    }
                }

                // Get the log file writer for this connection and create one if necessary.
                string loggerName = category;
                if (!StatLoggers.TryGetValue(loggerName, out connWriter))
                {
                    string headr = logPrefix;
                    headr = headr.Replace("%CATEGORY%", category);
                    headr = headr.Replace("%REGIONNAME%", RegionName);
                    connWriter = new LogWriter(logDir, headr, logFileTime, logFlushWrites);
                    StatLoggers.Add(loggerName, connWriter);

                    if (LogRegionIncludeTitleLine)
                    {
                        StringBuilder bufft = new StringBuilder();
                        bufft.Append("Category");
                        bufft.Append(",");
                        bufft.Append("RegionName");
                        foreach (string fld in fields)
                        {
                            bufft.Append(",");
                            bufft.Append(fld);
                        }

                        connWriter.Write(bufft.ToString());
                    }
                }

                StringBuilder buff = new StringBuilder();
                buff.Append(category);
                buff.Append(",");
                buff.Append(RegionName);
                foreach (string fld in fields)
                {
                    buff.Append(",");
                    buff.Append(outputValues.ContainsKey(fld) ? outputValues[fld] : "");
                }
                connWriter.Write(buff.ToString());
            }
        }

        long m_lastLLUDPAggregatedIn=0;
        long m_lastLLUDPAggregatedOut=0;
        int m_lastLLUDPStatsLogTime;
        LogWriter m_LLUDPStatsLogWriter = null;
        //int ticksPerSecond = 10000;
        int envTickPerSecond = 1000;

        private void LogLLUDPStats()
        {
            int msSinceLast = Util.EnvironmentTickCountSubtract(m_lastLLUDPStatsLogTime);


            if (msSinceLast > LogLLUDPBWAggInterval * envTickPerSecond)
            {
                long currentLLUDPAggregatedIn = LLUDPServer.AggregatedLLUDPBytesIn;
                long currentLLUDPAggregatedOut = LLUDPServer.AggregatedLLUDPBytesOut;

                double bytesInPerSec = 0, bytesOutPerSec = 0;
                double bpsIn, bpsOut;

                bytesInPerSec = (currentLLUDPAggregatedIn - m_lastLLUDPAggregatedIn) / ((double)msSinceLast / envTickPerSecond);
                bpsIn = bytesInPerSec * 8;

                bytesOutPerSec = (currentLLUDPAggregatedOut - m_lastLLUDPAggregatedOut) / ((double)msSinceLast / envTickPerSecond);
                bpsOut = bytesOutPerSec * 8;

                if (m_LLUDPStatsLogWriter == null)
                {
                    string headr = LogLLUDPBWAggFilenamePrefix;
                    headr = headr.Replace("%REGIONNAME%", RegionName);
                    m_LLUDPStatsLogWriter = new LogWriter(LogLLUDPBWAggDirectory, headr, LogLLUDPBWAggFileTimeMinutes, LogLLUDPBWAggFlushWrites);

                    //m_log.WarnFormat("SyncStats: create LLUDP stats file with header {0}", headr);
                }

                StringBuilder buff = new StringBuilder();
                buff.Append(RegionName);
                buff.Append(",");
                buff.Append(bytesInPerSec);
                buff.Append(",");
                buff.Append(bpsIn);
                buff.Append(",");
                buff.Append(bytesOutPerSec);
                buff.Append(",");
                buff.Append(bpsOut);
                m_LLUDPStatsLogWriter.Write(buff.ToString());

                m_lastLLUDPStatsLogTime = Util.EnvironmentTickCount();
                m_lastLLUDPAggregatedIn = currentLLUDPAggregatedIn;
                m_lastLLUDPAggregatedOut = currentLLUDPAggregatedOut;
            }
        }

        private void SetupRemoteStatsFetch()
        {
            if (!RemoteStatsFetchEnabled) return;

            string urlBase = String.Format("/{0}/", RemoteStatsFetchBase);
            MainServer.Instance.AddHTTPHandler(urlBase, HandleStatsRequest);
            m_log.DebugFormat("{0}: RemoteStatsFetch enabled. URL={1}", LogHeader, urlBase);
        }

        private Hashtable HandleStatsRequest(Hashtable request)
        {
            Hashtable responsedata = new Hashtable();
            string regpath = request["uri"].ToString();
            int response_code = 200;
            string contenttype = "text/json";

            string pCategoryName = StatsManager.AllSubCommand;
            string pContainerName = StatsManager.AllSubCommand;
            string pStatName = StatsManager.AllSubCommand;

            if (request.ContainsKey("cat")) pCategoryName = request["cat"].ToString();
            if (request.ContainsKey("cont")) pContainerName = request["cat"].ToString();
            if (request.ContainsKey("stat")) pStatName = request["cat"].ToString();

            string strOut = StatsManager.GetStatsAsOSDMap(pCategoryName, pContainerName, pStatName).ToString();

            // m_log.DebugFormat("{0} StatFetch: uri={1}, cat={2}, cont={3}, stat={4}, resp={5}",
            //                         LogHeader, regpath, pCategoryName, pContainerName, pStatName, strOut);

            responsedata["int_response_code"] = response_code;
            responsedata["content_type"] = contenttype;
            responsedata["keepalive"] = false;
            responsedata["str_response_string"] = strOut;
            responsedata["access_control_allow_origin"] = "*";

            return responsedata;
        }

    }
}
