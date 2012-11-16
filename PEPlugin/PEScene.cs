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
using Nini.Config;
using log4net;
using OpenSim.Framework;
using OpenSim.Region.Physics.Manager;
using OpenMetaverse;
using OpenSim.Region.Framework;
//using DSG.RegionSync;

namespace DSG.PEPlugin
{
public class PEScene : PhysicsScene
{
    private static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private List<PECharacter> m_avatars = new List<PECharacter>();
    private List<PEPrim> m_prims = new List<PEPrim>();
    private float[] m_heightMap;

    public PEScene(string identifier)
    {
    }

    public override void Initialise(IMesher meshmerizer, IConfigSource config)
    {
    }

    public override PhysicsActor AddAvatar(string avName, Vector3 position, Vector3 size, bool isFlying)
    {
        PECharacter actor = new PECharacter(avName, this, position, null, size, 0f, 0f, .5f, 1f,
            1f, 1f, .5f, .5f);
        lock (m_avatars) m_avatars.Add(actor);
        return actor;
    }

    public override void RemoveAvatar(PhysicsActor actor)
    {
        try
        {
            lock (m_avatars) m_avatars.Remove((PECharacter)actor);
        }
        catch (Exception e)
        {
            m_log.WarnFormat("[RPE]: Attempt to remove avatar that is not in physics scene: {0}", e);
        }
    }

    public override void RemovePrim(PhysicsActor prim)
    {
        try
        {
            lock (m_prims) m_prims.Remove((PEPrim)prim);
        }
        catch (Exception e)
        {
            m_log.WarnFormat("[RPE]: Attempt to remove prim that is not in physics scene: {0}", e);
        }
    }

    public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position,
                                              Vector3 size, Quaternion rotation, bool isPhysical, uint localID)
    {
        PEPrim prim = new PEPrim(primName, this, position, size, rotation, null, pbs, isPhysical, null);
        prim.LocalID = localID;
        lock (m_prims) m_prims.Add(prim);
        return prim;
    }

    public override void AddPhysicsActorTaint(PhysicsActor prim) { }

    public override float Simulate(float timeStep)
    {
        // let all of the avatars do updating
        lock (m_avatars)    // technically, locking is not requred
        {
            foreach (PECharacter actor in m_avatars)
            {
                // m_log.DebugFormat("[RPE]: Simulate. p={0}, a={1}", m_prims.Count, m_avatars.Count);
                actor.DoSimulateUpdate();
            }
        }

        return 60f;
    }

    public override void GetResults() { }

    public override void SetTerrain(float[] heightMap) {
        m_heightMap = heightMap;
    }

    public override void SetWaterLevel(float baseheight) { }

    public override void DeleteTerrain() { }

    public override void Dispose() { }

    public override Dictionary<uint, float> GetTopColliders()
    {
        return new Dictionary<uint, float>();
    }

    public override bool IsThreaded { get { return false;  } }

}
}
