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
using OpenSim.Region.CoreModules.Framework.Statistics;

using OpenMetaverse.StructuredData;

using log4net;
using Nini.Config;

namespace DSG.RegionSync
{
public class SyncStatisticCollector
{
    private readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private readonly string LogHeader = "[SYNC STATISTICS COLLECTOR]";

    private bool Enabled { get; set; }
    private int LogIntervalSeconds { get; set; }
    private string DSGCategory { get; set; }

    public SyncStatisticCollector(IConfig cfg)
    {
        Enabled = cfg.GetBoolean("StatisticLoggingEnable", false);
        if (Enabled)
        {
            DSGCategory = cfg.GetString("LogDSGCategory", "DSG");
            LogIntervalSeconds = cfg.GetInt("LogIntervalSeconds", 5);
        }
    }

    // Build an OSDMap of the DSG connector info. Returned map is of the form:
    //   { regionName: {
    //           connectorName: {
    //                "OtherSideRegion": name,
    //                "OtherSideActor": name,
    //                "DSG_Queued_Msgs": num,
    //                "DSG_Bytes_Sent": num,
    //                "DSG_Bytes_Rcvd": num,
    //                "DSG_Msgs_Sent": num,
    //                "DSG_Msgs_Rcvd": num,
    //                "MessagesByType": {
    //                      typeName: {
    //                           "DSG_Msgs_Typ_Rcvd": num,
    //                           "DSG_Msgs_Typ_Sent": num,
    //                      }
    //                      ...
    //                }
    //           }
    //           ...
    //     }
    //     ...
    //   }
    
    private OSDMap GetConnectors()
    {
        OSDMap ret = new OSDMap();

        // Fetch all the DSG stats. Extract connectors and then organize the stats.
        // The top dictionary is the containers (region name)
        SortedDictionary<string, SortedDictionary<string, Stat>> DSGStats;
        if (StatsManager.TryGetStats(DSGCategory, out DSGStats))
        {
            foreach (string container in DSGStats.Keys)
            {
                OSDMap containerMap = new OSDMap();
                foreach (KeyValuePair<string, Stat> aStat in DSGStats[container])
                {
                    if (Regex.IsMatch(aStat.Value.ShortName, "SyncConnector"))
                    {
                        // Short names are like
                        //       "DSG_Bytes_Sent|SyncConnector2(physics/rpea00)"
                        //       "DSG_Bytes_Typ_Sent|SyncConnector2(physics/rpea00)|GetObjects"
                        string[] shortNamePieces = aStat.Value.ShortName.Split('|');
                        if (shortNamePieces.Length > 0)
                        {
                            string statName = shortNamePieces[0];
                            string connectorName = String.Empty;
                            string otherSideActor = String.Empty;
                            string otherSideRegion = string.Empty;
                            string messageType = string.Empty;
                            try
                            {
                                if (shortNamePieces.Length >= 2)
                                {
                                    string[] connectorPieces = shortNamePieces[1].Split('(');
                                    if (connectorPieces.Length > 1)
                                    {
                                        connectorName = connectorPieces[0];
                                        string[] otherSidePieces = connectorPieces[1].Split('/');
                                        if (otherSidePieces.Length > 1)
                                        {
                                            otherSideActor = otherSidePieces[0];
                                            // remove the trailing ')'
                                            otherSideRegion = otherSidePieces[1].Remove(otherSidePieces.Length - 1);
                                        }
                                    }
                                }
                                if (shortNamePieces.Length > 2)
                                {
                                    messageType = shortNamePieces[2];
                                }
                            }
                            catch (Exception e)
                            {
                                m_log.ErrorFormat("{0} Exception parsing DSG stats. shortName={1},statName={2},conn={3},e={4}",
                                                        LogHeader, aStat.Value.ShortName, statName, connectorName, e);
                            }
                            try
                            {
                                float val = (float)aStat.Value.Value;
                                if (!containerMap.ContainsKey(connectorName))
                                {
                                    OSDMap connectorNew = new OSDMap();
                                    connectorNew.Add("OtherSideActor", otherSideActor);
                                    connectorNew.Add("OtherSideRegion", otherSideRegion);
                                    connectorNew.Add("MessagesByType", new OSDMap());
                                    containerMap.Add(connectorName, connectorNew);
                                }
                                OSDMap connectorMap = (OSDMap)containerMap[connectorName];
                                connectorMap.Add(statName, OSD.FromReal(val));
                                if (!string.IsNullOrEmpty(messageType))
                                {
                                    OSDMap messagesMap = (OSDMap)connectorMap["MessagesByType"];
                                    if (!messagesMap.ContainsKey(messageType))
                                    {
                                        messagesMap.Add(messageType, new OSDMap());
                                    }
                                    OSDMap messageMap = (OSDMap)messagesMap[messageType];
                                    messagesMap.Add(statName, OSD.FromReal(val));
                                }
                            }
                            catch (Exception e)
                            {
                                m_log.ErrorFormat("{0} Exception adding stat to block. name={1}, block={2}, e={3}",
                                                        LogHeader, aStat.Value.ShortName, OSDParser.SerializeJsonString(containerMap), e);
                            }
                        }
                    }
                }
                ret.Add(container, containerMap);
            }
        }
        return ret;
    }

    // Temporary place while this code is developed.
    // Will eventually move to OpenSim.Framework.Monitoring.Stats
    // Create a time histogram of events. The histogram is built in a wrap-around
    //   array of equally distributed buckets.
    // For instance, a minute long histogram of second sized buckets would be:
    //          new EventHistogram(60, 1000)
    public class EventHistogram
    {
        private int m_timeBase;
        private int m_numBuckets;
        private int m_bucketMilliseconds;
        private int m_lastBucket;
        private int m_totalHistogramMilliseconds;
        private int[] m_histogram;

        public EventHistogram(int numberOfBuckets, int millisecondsPerBucket)
        {
            m_numBuckets = numberOfBuckets;
            m_bucketMilliseconds = millisecondsPerBucket;
            m_totalHistogramMilliseconds = m_numBuckets * m_bucketMilliseconds;

            m_histogram = new int[m_numBuckets];
            Zero();
            m_lastBucket = 0;
            m_timeBase = Util.EnvironmentTickCount();
        }

        // Record an event at time 'now' in the histogram.
        public void Event()
        {
            // The time as displaced from the base of the histogram
            int bucketTime = Util.EnvironmentTickCountSubtract(m_timeBase);

            // If more than the total time of the histogram, we just start over
            if (bucketTime > m_totalHistogramMilliseconds)
            {
                Zero();
                m_lastBucket = 0;
                m_timeBase = Util.EnvironmentTickCount();
            }
            else
            {
                // To which bucket should we add this event?
                int bucket = bucketTime / m_bucketMilliseconds;

                // Advance m_lastBucket to the new bucket. Zero any buckets skipped over.
                while (bucket != m_lastBucket)
                {
                    // Zero from just after the last bucket to the new bucket or the end
                    for (int jj = m_lastBucket + 1; jj <= Math.Min(bucket, m_numBuckets-1); jj++)
                    {
                        m_histogram[jj] = 0;
                    }
                    m_lastBucket = bucket;
                    // If the new bucket is off the end, wrap around to the beginning
                    if (bucket > m_numBuckets)
                    {
                        bucket -= m_numBuckets;
                        m_lastBucket = 0;
                        m_histogram[m_lastBucket] = 0;
                        m_timeBase += m_totalHistogramMilliseconds;
                    }
                }
            }
            m_histogram[m_lastBucket] += 1;
        }

        // Get a copy of the current histogram
        public int[] GetHistogram()
        {
            int[] ret = new int[m_numBuckets];
            int indx = m_lastBucket + 1;
            for (int ii = 0; ii < m_numBuckets; ii++, indx++)
            {
                if (indx >= m_numBuckets)
                    indx = 0;
                ret[ii] = m_histogram[indx];
            }
            return ret;
        }

        // Zero out the histogram
        public void Zero()
        {
            for (int ii = 0; ii < m_numBuckets; ii++)
                m_histogram[ii] = 0;
        }
    }

}
}
