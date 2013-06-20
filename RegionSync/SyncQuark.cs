/* Copyright 2011 (c) Intel Corporation
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * The name of the copyright holder may not be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;

/////////////////////////////////////////////////////////////////////////////////////////////
//KittyL: created 05/23/2011, to start "Quark based load assignment and DSG synchronization"
/////////////////////////////////////////////////////////////////////////////////////////////
namespace DSG.RegionSync
{
    public class SyncQuark
    {
        public static int SizeX = 256; // Must be set by QuarkManager. Default will be 256.
        public static int SizeY = 256;
        public bool ValidQuark = false; //"false" indicates having not been assigned a location yet
        private int m_quarkLocX;
        private int m_quarkLocY;
        private string m_quarkName = String.Empty;

        public int QuarkLocX
        {
            get { return m_quarkLocX; }
            //set {m_quarkLocX = value;}
        }
        public int QuarkLocY
        {
            get { return m_quarkLocY; }
            //set {m_quarkLocY = value;}
        }
        public string QuarkName
        {
            get { return m_quarkName; }
        }

        public SyncQuark(string quarkName)
        {
            m_quarkName = quarkName;
            DecodeSyncQuarkLoc();
            ComputeMinMax();
        }

        // Create a SyncQuark given a position in the region domain
        public SyncQuark(Vector3 pos)
        {
            int locX, locY;
            GetQuarkLocByPosition(pos, out locX, out locY);
            m_quarkLocX = locX;
            m_quarkLocY = locY;
            ValidQuark = true;
            m_quarkName = SyncQuarkLocToName(locX, locY);
            ComputeMinMax();
        }

        private void ComputeMinMax()
        {
            if (ValidQuark)
            {
                m_minX = m_quarkLocX * SizeX;
                m_minY = m_quarkLocY * SizeY;

                m_maxX = m_minX + SizeX;
                m_maxY = m_minY + SizeY;
            }
        }

        public override bool Equals(Object other)
        {
            if (other != null && other is SyncQuark)
            {
                SyncQuark sq = other as SyncQuark;
                return (this.m_quarkLocX == sq.m_quarkLocX) && (this.m_quarkLocY == sq.m_quarkLocY);
            }
            return false;
        }

        public override string ToString()
        {
            return m_quarkName;
        }

        public override int GetHashCode()
        {
            return m_quarkName.GetHashCode();
        }

        #region Util functions
        public static void SyncQuarkNameToLoc(string quarkName, out int quarkLocX, out int quarkLocY)
        {
            quarkLocX = -1;
            quarkLocY = -1;

            string[] stringItems = quarkName.Split(',');
            if (stringItems.Length != 2)
                return;

            quarkLocX = Convert.ToInt32(stringItems[0]);
            quarkLocY = Convert.ToInt32(stringItems[1]);
        }

        public static string SyncQuarkLocToName(int qLocX, int qlocY)
        {
            string quarkName = qLocX + "," + qlocY;
            return quarkName;
        }

        /// <summary>
        /// Given a global coordinate, return the location of the quark the 
        /// position resides in. Assumption: 
        /// (1) All quarks fit within one OpenSim
        /// region. Hence, Constants.RegionSize is the max value for both x,y
        /// coordinates of a position.
        /// (2) The base locX,Y values are (0,0).
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="quarkLocX"></param>
        /// <param name="quarkLocY"></param>
        public static void GetQuarkLocByPosition(Vector3 pos, out int quarkLocX, out int quarkLocY)
        {
            quarkLocX = (int)(pos.X / SizeX);
            quarkLocY = (int)(pos.Y / SizeY);
        }

        public static string GetQuarkNameByPosition(Vector3 pos)
        {
            if (pos == null)
                pos = new Vector3(0, 0, 0);
            int qLocX, qLocY;
            GetQuarkLocByPosition(pos, out qLocX, out qLocY);
            string quarkName = SyncQuarkLocToName(qLocX, qLocY);
            return quarkName;
        }

        #endregion

        #region SyncQuark Members and functions

        private int m_minX, m_minY, m_maxX, m_maxY;

        public bool IsPositionInQuark(Vector3 pos)
        {
            if (pos.X >= m_minX && pos.X < m_maxX && pos.Y >= m_minY && pos.Y < m_maxY)
            {
                return true;
            }
            return false;
        }

        private void DecodeSyncQuarkLoc()
        {
            int qLocX, qLocY;
            SyncQuarkNameToLoc(m_quarkName, out qLocX, out qLocY);

            if (qLocX == -1)
            {
                ValidQuark = false;
                return;
            }

            m_quarkLocX = qLocX;
            m_quarkLocY = qLocY;
            ValidQuark = true;
        }

        #endregion
    }

}