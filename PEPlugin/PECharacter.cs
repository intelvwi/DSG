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
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Physics.Manager;
//using DSG.RegionSync;

namespace DSG.PEPlugin
{
public class PECharacter : PhysicsActor
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private bool _stopped;
    private Vector3 _size;
    private PrimitiveBaseShape _pbs;
    private uint _localID = 0;
    private bool _grabbed;
    private bool _selected;
    private Vector3 _position;
    private float _mass = 80f;
    public float _density = 60f;
    public float CAPSULE_RADIUS = 0.37f;
    public float CAPSULE_LENGTH = 2.140599f;
    private Vector3 _force;
    private Vector3 _targetVelocity;
    private Vector3 _velocity;
    private Vector3 _torque;
    private float _collisionScore;
    private Vector3 _acceleration;
    private Quaternion _orientation;
    private int _physicsActorType;
    private bool _isPhysical;
    private bool _flying;
    private bool _setAlwaysRun;
    private bool _throttleUpdates;
    private bool _isColliding;
    private bool _collidingGround;
    private bool _collidingObj;
    private bool _floatOnWater;
    private Vector3 _rotationalVelocity;
    private bool _kinematic;
    private float _buoyancy;

    private Vector3 _PIDTarget;
    private bool _usePID;
    private float _PIDTau;
    private bool _useHoverPID;
    private float _PIDHoverHeight;
    private PIDHoverType _PIDHoverType;
    private float _PIDHoverTao;

    public PECharacter(String avName, PEScene parent_scene, Vector3 pos, 
                    CollisionLocker dode, Vector3 size, float pid_d, float pid_p, 
                    float capsule_radius, float tensor, float density, float height_fudge_factor, 
                    float walk_divisor, float rundivisor)
    {
        _position = pos;
        _size = size;
        _density = density;
        //base.ChangingActorID = RegionSyncServerModule.ActorID;
        //return;
    }
    /*
        public override void RequestPhysicsterseUpdate()
        {
            if (PhysEngineToSceneConnectorModule.IsPhysEngineActorS)
            {
                // if the values have changed and it was I who changed them, send an update
                if (this.lastValues.Changed(this) && ChangingActorID == RegionSyncServerModule.ActorID)
                {
                    // m_log.DebugFormat("[ODE CHARACTER]: Sending terse update for {0}", LocalID);
                    PhysEngineToSceneConnectorModule.RouteUpdate(this);
                }
            }
            else
            {
                base.RequestPhysicsterseUpdate();
            }
        }
    */

    public override bool Stopped { 
        get { return _stopped; } 
    }
    public override Vector3 Size { 
        get { return _size; } 
        set { _size = value;
        } 
    }
    public override PrimitiveBaseShape Shape { 
        set { _pbs = value; 
        } 
    }
    public override uint LocalID { 
        set { _localID = value; 
        }
        get { return _localID; }
    }
    public override bool Grabbed { 
        set { _grabbed = value; 
            // m_log.Debug("[RPE] PEChar set Grabbed");
        } 
    }
    public override bool Selected { 
        set { _selected = value; 
            // m_log.Debug("[RPE] PEChar set Selected");
        } 
    }
    public override void CrossingFailure() { return; }
    public override void link(PhysicsActor obj) { return; }
    public override void delink() { return; }
    public override void LockAngularMotion(Vector3 axis) { return; }

    public override Vector3 Position { 
        get { return _position; } 
        set { _position = value; 
            //base.ChangingActorID = RegionSyncServerModule.ActorID;
        } 
    }
    public override float Mass { 
        get { 
            float AVvolume = (float) (Math.PI*Math.Pow(CAPSULE_RADIUS, 2)*CAPSULE_LENGTH);
            _mass = _density*AVvolume;
            return _mass; 
        } 
    }
    public override Vector3 Force { 
        get { return _force; } 
        set { _force = value; 
            //base.ChangingActorID = RegionSyncServerModule.ActorID;
        } 
    }

    public override int VehicleType { 
        get { return 0; } 
        set { return; } 
    }
    public override void VehicleFloatParam(int param, float value) { }
    public override void VehicleVectorParam(int param, Vector3 value) {}
    public override void VehicleRotationParam(int param, Quaternion rotation) { }
    public override void VehicleFlags(int param, bool remove) { }

    // Allows the detection of collisions with inherently non-physical prims. see llVolumeDetect for more
    public override void SetVolumeDetect(int param) { return; }

    public override Vector3 GeometricCenter { get { return Vector3.Zero; } }
    public override Vector3 CenterOfMass { get { return Vector3.Zero; } }
    public override Vector3 TargetVelocity
    {
        get {
            //m_log.WarnFormat("PECharacter GET TargetVelocity = {0}", _targetVelocity);
            return _targetVelocity;
        }
        set {
            Vector3 old = _targetVelocity;
            _targetVelocity = value;
            //if(old != _targetVelocity)
            //    m_log.WarnFormat("PECharacter SET TargetVelocity from {0} to {1}", old, _targetVelocity);
        }
    }

    public override Vector3 Velocity
    {
        get
        {
            //m_log.WarnFormat("PECharacter GET Velocity = {0}", _velocity);
            return _velocity;
        }
        set
        {
            Vector3 old = _velocity;
            _velocity = value;
            //if (old != _velocity)
            //    m_log.WarnFormat("PECharacter SET Velocity from {0} to {1}", old, _velocity);
        }
    }


    public override Vector3 Torque { 
        get { return _torque; } 
        set { _torque = value; 
        } 
    }
    public override float CollisionScore { 
        get { return _collisionScore; } 
        set { _collisionScore = value; 
        } 
    }
    public override Vector3 Acceleration { 
        get { return _acceleration; } 
        set { _acceleration = value; } 
    }
    public override Quaternion Orientation { 
        get { return _orientation; } 
        set { _orientation = value; 
            //base.ChangingActorID = RegionSyncServerModule.ActorID;
        } 
    }
    public override int PhysicsActorType { 
        get { return _physicsActorType; } 
        set { _physicsActorType = value; 
        } 
    }
    public override bool IsPhysical { 
        get { return _isPhysical; } 
        set { _isPhysical = value; 
        } 
    }
    public override bool Flying { 
        get { return _flying; } 
        set { _flying = value; 
        } 
    }
    public override bool 
        SetAlwaysRun { 
        get { return _setAlwaysRun; } 
        set { _setAlwaysRun = value; } 
    }
    public override bool ThrottleUpdates { 
        get { return _throttleUpdates; } 
        set { _throttleUpdates = value; } 
    }
    public override bool IsColliding { 
        get { return _isColliding; } 
        set { _isColliding = value; } 
    }
    public override bool CollidingGround { 
        get { return _collidingGround; } 
        set { _collidingGround = value; } 
    }
    public override bool CollidingObj { 
        get { return _collidingObj; } 
        set { _collidingObj = value; } 
    }
    public override bool FloatOnWater { 
        set { _floatOnWater = value; } 
    }
    public override Vector3 RotationalVelocity { 
        get { return _rotationalVelocity; } 
        set { _rotationalVelocity = value; } 
    }
    public override bool Kinematic { 
        get { return _kinematic; } 
        set { _kinematic = value; } 
    }
    public override float Buoyancy { 
        get { return _buoyancy; } 
        set { _buoyancy = value; } 
    }

    // Used for MoveTo
    public override Vector3 PIDTarget { 
        set { _PIDTarget = value; } 
    }
    public override bool PIDActive { 
        set { _usePID = value; } 
    }
    public override float PIDTau { 
        set { _PIDTau = value; } 
    }

    // Used for llSetHoverHeight and maybe vehicle height
    // Hover Height will override MoveTo target's Z
    public override bool PIDHoverActive { 
        set { _useHoverPID = value; }
    }
    public override float PIDHoverHeight { 
        set { _PIDHoverHeight = value; }
    }
    public override PIDHoverType PIDHoverType { 
        set { _PIDHoverType = value; }
    }
    public override float PIDHoverTau { 
        set { _PIDHoverTao = value; }
    }

    // For RotLookAt
    public override Quaternion APIDTarget { set { return; } }
    public override bool APIDActive { set { return; } }
    public override float APIDStrength { set { return; } }
    public override float APIDDamping { set { return; } }

    public override void AddForce(Vector3 force, bool pushforce) { 
        if (force.IsFinite())
        {
            // base.ChangingActorID = RegionSyncServerModule.ActorID;
            _velocity.X += force.X;
            _velocity.Y += force.Y;
            _velocity.Z += force.Z;
        }
        else
        {
            m_log.Warn("[PHYSICS]: Got a NaN force applied to a Character");
        }
        //m_lastUpdateSent = false;
    }
    public override void AddAngularForce(Vector3 force, bool pushforce) { 
    }
    public override void SetMomentum(Vector3 momentum) { 
    }
    private int eventOccuranceLimit = 0;
    private int timeForNextCollisionEvent = 0;
    public override void SubscribeEvents(int ms) {
        eventOccuranceLimit = ms;
    }
    public override void UnSubscribeEvents() {
        eventOccuranceLimit = 0;
    }
    public override bool SubscribedEvents() { 
        return (eventOccuranceLimit != 0); 
    }

    // Called by PEScene on every simulate tick.
    // To make animations work, we need to generate a collision with
    //   an empty list of collisions.
    public void DoSimulateUpdate()
    {
        if (eventOccuranceLimit > 0)
        {
            int now = Util.EnvironmentTickCount();
            if (now > timeForNextCollisionEvent)
            {
                timeForNextCollisionEvent = now + eventOccuranceLimit;
                base.SendCollisionUpdate(new CollisionEventUpdate());
            }
        }

    }

}
}
