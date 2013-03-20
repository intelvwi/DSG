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
using System.Threading;
using System.Linq;
using System.Text;
using OpenMetaverse;

namespace DSG.RegionSync
{
    // Class queues updates for UUID's. 
    // The updates could be JSON, serialized object data, or any string. 
    // Updates are queued to the end and dequeued from the front of the queue
    // Enqueuing an update with the same UUID will replace the previous update
    // so it will not lose its place.
    class BlockingUpdateQueue
    {
        private object m_syncRoot = new object();
        private Queue<SymmetricSyncMessage> m_firstQueue = new Queue<SymmetricSyncMessage>();
        private Queue<UUID> m_queue = new Queue<UUID>();
        private Dictionary<UUID, SymmetricSyncMessage> m_updates = new Dictionary<UUID, SymmetricSyncMessage>();

        // The number of times we throw away an old update for the same UUID
        public long OverWrittenUpdates = 0;

        // Enqueue an update
        // Note that only one update for each id is queued so it is possible that this particular
        //      update will not get queued if there is already one queued for that id.
        // Returns 'true' if the object was actually enqueued.
        public bool Enqueue(UUID id, SymmetricSyncMessage update)
        {
            bool ret = false;
            lock(m_syncRoot)
            {
                if (!m_updates.ContainsKey(id))
                {
                    m_queue.Enqueue(id);
                    ret = true;
                }
                else
                {
                    OverWrittenUpdates++;
                }
                m_updates[id] = update;
                Monitor.Pulse(m_syncRoot);
            }
            return ret;
        }

        // Add a message to the first of the queue.
        public void QueueMessageFirst(SymmetricSyncMessage update)
        {
            lock (m_syncRoot)
            {
                m_firstQueue.Enqueue(update);
                Monitor.Pulse(m_syncRoot);
            }
        }

        // Dequeue an update
        public SymmetricSyncMessage Dequeue()
        {
            SymmetricSyncMessage update = null;
            lock (m_syncRoot)
            {
                // If the queue is empty, wait for it to contain something
                while (m_queue.Count == 0 && m_firstQueue.Count == 0)
                    Monitor.Wait(m_syncRoot);
                if (m_firstQueue.Count > 0)
                {
                    update = m_firstQueue.Dequeue();
                }
                else
                {
                    UUID id = m_queue.Dequeue();
                    update = m_updates[id];
                    m_updates.Remove(id);
                }
            }
            return update;
        }

        // Count of number of items currently queued
        public int Count
        {
            get
            {
                lock (m_syncRoot)
                    return m_queue.Count;
            }
        }
    }
}
