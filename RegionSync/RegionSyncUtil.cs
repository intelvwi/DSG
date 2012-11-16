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
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using OpenMetaverse.StructuredData;
using log4net;

using OpenSim.Framework;
using OpenMetaverse;

namespace DSG.RegionSync
{
    class RegionSyncUtil
    {
        // The logfile
        private static ILog m_log;

        //HashSet<string> exceptions = new HashSet<string>();
        public static OSDMap DeserializeMessage(SymmetricSyncMessage msg, string logHeader)
        {
            OSDMap data = null;
            try
            {
                data = OSDParser.DeserializeJson(Encoding.ASCII.GetString(msg.Data, 0, msg.Length)) as OSDMap;
            }
            catch (Exception e)
            {
                m_log.Error(logHeader + " " + Encoding.ASCII.GetString(msg.Data, 0, msg.Length));
                data = null;
            }
            return data;
        }
    }

    /// <summary>
    /// Class for writing a log file. Create a new instance with the parameters needed and
    /// call Write() to output a line. Call Close() when finished.
    /// If created with no parameters, it will not log anything.
    /// </summary>
    public class LogWriter : IDisposable
    {
        private bool m_logEnabled = false;
        public bool Enabled
        {
            get { return m_logEnabled; }
        }

        private bool m_flushWrites = false;
        private string m_logDirectory = ".";
        private int m_logMaxFileTimeMin = 5;    // 5 minutes
        private string m_logFileHeader = "log-";
        public String LogFileHeader
        {
            get { return m_logFileHeader; }
            set { m_logFileHeader = value; }
        }

        private StreamWriter m_logFile = null;
        private TimeSpan m_logFileLife;
        private DateTime m_logFileEndTime;
        private Object m_logFileWriteLock = new Object();

        public ILog ErrorLogger = null;
        private string LogHeader = "[LOG WRITER]";

        /// <summary>
        /// Create a log writer that will not write anything. Good for when not enabled
        /// but the write statements are still in the code.
        /// </summary>
        public LogWriter()
        {
            m_logEnabled = false;
            m_logFile = null;
        }

        /// <summary>
        /// Create a log writer instance.
        /// </summary>
        /// <param name="dir">The directory to create the log file in. May be 'null' for default.</param>
        /// <param name="headr">The characters that begin the log file name. May be 'null' for default.</param>
        /// <param name="maxFileTime">Maximum age of a log file in minutes. If zero, will set default.</param>
        public LogWriter(string dir, string headr, int maxFileTime, bool flushWrites)
        {
            m_logDirectory = dir == null ? "." : dir;

            m_logFileHeader = headr == null ? "log-" : headr;

            m_logMaxFileTimeMin = maxFileTime;
            if (m_logMaxFileTimeMin < 1)
                m_logMaxFileTimeMin = 5;

            m_logFileLife = new TimeSpan(0, m_logMaxFileTimeMin, 0);
            m_logFileEndTime = DateTime.Now + m_logFileLife;

            m_flushWrites = flushWrites;

            m_logEnabled = true;
        }

        public void Dispose()
        {
            this.Close();
        }

        public void Close()
        {
            m_logEnabled = false;
            if (m_logFile != null)
            {
                m_logFile.Close();
                m_logFile.Dispose();
                m_logFile = null;
            }
        }

        public void Write(string line, params object[] args)
        {
            if (!m_logEnabled) return;
            Write(String.Format(line, args));
        }

        public void Write(string line)
        {
            if (!m_logEnabled) return;
            try
            {
                lock (m_logFileWriteLock)
                {
                    DateTime now = DateTime.Now;
                    if (m_logFile == null || now > m_logFileEndTime)
                    {
                        if (m_logFile != null)
                        {
                            m_logFile.Close();
                            m_logFile.Dispose();
                            m_logFile = null;
                        }

                        // First log file or time has expired, start writing to a new log file
                        m_logFileEndTime = now + m_logFileLife;
                        string path = (m_logDirectory.Length > 0 ? m_logDirectory
                                    + System.IO.Path.DirectorySeparatorChar.ToString() : "")
                                + String.Format("{0}{1}.log", m_logFileHeader, now.ToString("yyyyMMddHHmmss"));
                        m_logFile = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read));
                    }
                    if (m_logFile != null)
                    {
                        StringBuilder buff = new StringBuilder();
                        buff.Append(now.ToString("yyyyMMddHHmmssfff"));
                        // buff.Append(now.ToString("yyyyMMddHHmmss"));
                        buff.Append(",");
                        buff.Append(line);
                        buff.Append("\r\n");
                        m_logFile.Write(buff.ToString());
                        if(m_flushWrites)
                            m_logFile.Flush();
                    }
                }
            }
            catch (Exception e)
            {
                if (ErrorLogger != null)
                {
                    ErrorLogger.ErrorFormat("{0}: FAILURE WRITING TO LOGFILE: {1}", LogHeader, e);
                }
                m_logEnabled = false;
            }
            return;
        }
    }
}
