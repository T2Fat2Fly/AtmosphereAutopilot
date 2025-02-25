/*
Atmosphere Autopilot, plugin for Kerbal Space Program.
Copyright (C) 2015-2016, Baranin Alexander aka Boris-Barboris.

Atmosphere Autopilot is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
Atmosphere Autopilot is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.
You should have received a copy of the GNU General Public License
along with Atmosphere Autopilot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using UnityEngine;

namespace AtmosphereAutopilot
{
    public struct Waypoint
    {
        public Waypoint(double longt, double lat)
        {
            longitude = longt;
            latitude = lat;
        }
        public double longitude;
        public double latitude;
    }

    /// <summary>
    /// Manages cruise flight modes, like heading and altitude holds
    /// </summary>
    public sealed class CruiseController : StateController
    {
        internal CruiseController(Vessel v)
            : base(v, "Cruise Flight controller", 88437226)
        {
            flc_controller.KP = 0.6;
            flc_controller.KI = 0.03;
            flc_controller.KD = 0.2;
            flc_controller.IntegralClamp = double.PositiveInfinity;
            flc_controller.AccumulatorClamp = double.PositiveInfinity;
        }

        FlightModel imodel;
        DirectorController dir_c;
        ProgradeThrustController thrust_c;

        public override void InitializeDependencies(Dictionary<Type, AutopilotModule> modules)
        {
            imodel = modules[typeof(FlightModel)] as FlightModel;
            dir_c = modules[typeof(DirectorController)] as DirectorController;
            thrust_c = modules[typeof(ProgradeThrustController)] as ProgradeThrustController;
        }

        protected override void OnActivate()
        {
            dir_c.Activate();
            thrust_c.Activate();
            imodel.Activate();
            MessageManager.post_status_message("Cruise Flight enabled");

            // let's set new circle axis
            if (vessel.srfSpeed > 5.0)
                circle_axis = Vector3d.Cross(vessel.srf_velocity, vessel.GetWorldPos3D() - vessel.mainBody.position).normalized;
            else
                circle_axis = Vector3d.Cross(vessel.ReferenceTransform.up, vessel.GetWorldPos3D() - vessel.mainBody.position).normalized;
        }

        protected override void OnDeactivate()
        {
            dir_c.Deactivate();
            thrust_c.Deactivate();
            imodel.Deactivate();
            MessageManager.post_status_message("Cruise Flight disabled");
        }

        Vector3d desired_velocity = Vector3d.zero;
        Vector3d planet2ves = Vector3d.zero;
        Vector3d planet2vesNorm = Vector3d.zero;
        Vector3d desired_vert_acc = Vector3d.zero;

        // centrifugal acceleration to stay on desired altitude
        Vector3d level_acc = Vector3d.zero;

        public override void ApplyControl(FlightCtrlState cntrl)
        {
            if (vessel.LandedOrSplashed())
                return;

            if (thrust_c.spd_control_enabled)
                thrust_c.ApplyControl(cntrl, thrust_c.setpoint.mps());

            desired_velocity = Vector3d.zero;
            planet2ves = vessel.ReferenceTransform.position - vessel.mainBody.position;
            planet2vesNorm = planet2ves.normalized;
            desired_vert_acc = Vector3d.zero;

            // centrifugal acceleration to stay on desired altitude
            level_acc = -planet2vesNorm * (imodel.surface_v - Vector3d.Project(imodel.surface_v, planet2vesNorm)).sqrMagnitude / planet2ves.magnitude;

            switch (current_mode)
            {
                default:
                case CruiseMode.LevelFlight:
                    // simply select velocity from axis
                    desired_velocity = Vector3d.Cross(planet2vesNorm, circle_axis);
                    handle_wide_turn();
                    if (vertical_control)
                    {
                        switch (height_mode)
                        {
                            case HeightMode.Altitude:
                                desired_velocity = account_for_height(desired_velocity);
                                break;
                            case HeightMode.VerticalSpeed:
                                desired_velocity = account_for_vertical_vel(desired_velocity);
                                break;
                            case HeightMode.FlightPathAngle:
                                desired_velocity = account_for_fpa(desired_velocity);
                                break;
                        }
                    }
                    break;

                case CruiseMode.CourseHold:
                    if (Math.Abs(vessel.latitude) > 80.0)
                    {
                        // we're too close to poles, let's switch to level flight
                        LevelFlightMode = true;
                        goto case CruiseMode.LevelFlight;
                    }
                    // get direction vector form course
                    Vector3d north = vessel.mainBody.RotationAxis;
                    Vector3d north_projected = Vector3.ProjectOnPlane(north, planet2vesNorm);
                    QuaternionD rotation = QuaternionD.AngleAxis(desired_course, planet2vesNorm);
                    desired_velocity = rotation * north_projected;
                    handle_wide_turn();
                    if (vertical_control)
                    {
                        switch (height_mode)
                        {
                            case HeightMode.Altitude:
                                desired_velocity = account_for_height(desired_velocity);
                                break;
                            case HeightMode.VerticalSpeed:
                                desired_velocity = account_for_vertical_vel(desired_velocity);
                                break;
                            case HeightMode.FlightPathAngle:
                                desired_velocity = account_for_fpa(desired_velocity);
                                break;
                        }
                    }
                    break;

                case CruiseMode.Waypoint:
                    // set new axis
                    Vector3d world_target_pos = vessel.mainBody.GetWorldSurfacePosition(desired_latitude, desired_longitude, vessel.altitude);
                    dist_to_dest = Vector3d.Distance(world_target_pos, vessel.ReferenceTransform.position);
                    if (dist_to_dest > 10000.0)
                    {
                        double radius = vessel.mainBody.Radius;
                        dist_to_dest = Math.Acos(1 - (dist_to_dest * dist_to_dest) / (2 * radius * radius)) * radius;
                    }
                    if (dist_to_dest < 200.0)
                    {
                        // we're too close to target, let's switch to level flight
                        LevelFlightMode = true;
                        picking_waypoint = false;
                        MessageManager.post_quick_message("Waypoint reached");
                        goto case CruiseMode.LevelFlight;
                    }
                    // set new axis according to waypoint
                    circle_axis = Vector3d.Cross(world_target_pos - vessel.mainBody.position, vessel.GetWorldPos3D() - vessel.mainBody.position).normalized;
                    goto case CruiseMode.LevelFlight;
            }

            if (use_keys)
            {
                ControlUtils.neutralize_user_input(cntrl, PITCH);
                ControlUtils.neutralize_user_input(cntrl, YAW);
            }

            double old_str = dir_c.strength;
            dir_c.strength *= strength_mult;
            dir_c.ApplyControl(cntrl, desired_velocity, level_acc + desired_vert_acc);
            dir_c.strength = old_str;
        }

        void handle_wide_turn()
        {
            Vector3d hor_vel = imodel.surface_v - Vector3d.Project(imodel.surface_v, planet2vesNorm);
            if (Vector3d.Dot(hor_vel.normalized, desired_velocity.normalized) < Math.Cos(0.5))
            {
                // we're turning for more than 45 degrees, let's force the turn to be horizontal
                Vector3d right_turn = Vector3d.Cross(planet2vesNorm, imodel.surface_v);
                double sign = Math.Sign(Vector3d.Dot(right_turn, desired_velocity));
                if (sign == 0.0)
                    sign = 1.0;
                desired_velocity = right_turn.normalized * sign * Math.Tan(0.5) + hor_vel.normalized;
            }
        }

        public enum CruiseMode
        {
            LevelFlight,
            CourseHold,
            Waypoint
        }

        public CruiseMode current_mode = CruiseMode.LevelFlight;

        public enum HeightMode
        {
            Altitude,
            VerticalSpeed,
            FlightPathAngle
        }

        public HeightMode height_mode = HeightMode.Altitude;

        public Waypoint current_waypt = new Waypoint();

        // axis to rotate around in level flight mode
        public Vector3d circle_axis = Vector3d.zero;

        [AutoGuiAttr("Director controller GUI", true)]
        public bool DircGUI { get { return dir_c.IsShown(); } set { if (value) dir_c.ShowGUI(); else dir_c.UnShowGUI(); } }

        [AutoGuiAttr("Thrust controller GUI", true)]
        public bool PTCGUI { get { return thrust_c.IsShown(); } set { if (value) thrust_c.ShowGUI(); else thrust_c.UnShowGUI(); } }

        [VesselSerializable("desired_course_field")]
        public DelayedFieldFloat desired_course = new DelayedFieldFloat(90.0f, "G4");

        [VesselSerializable("desired_latitude_field")]
        public DelayedFieldFloat desired_latitude = new DelayedFieldFloat(-0.0486178f, "#0.0000", DelayedFieldFloat.CoordFormat.NS);  // latitude of KSC runway, west end (default position for launched vessels)

        [VesselSerializable("desired_longitude_field")]
        public DelayedFieldFloat desired_longitude = new DelayedFieldFloat(-74.72444f, "#0.0000", DelayedFieldFloat.CoordFormat.EW);  // longitude of KSC runway, west end (default position for launched vessels)

        [VesselSerializable("vertical_control")]
        public bool vertical_control = false;

        [VesselSerializable("desired_altitude_field")]
        public DelayedFieldFloat desired_altitude = new DelayedFieldFloat(1000.0f, "G5");

        [VesselSerializable("desired_vertspeed_field")]
        public DelayedFieldFloat desired_vertspeed = new DelayedFieldFloat(0.0f, "G4");

        [GlobalSerializable("pseudo_flc")]
        [VesselSerializable("pseudo_flc")]
        [AutoGuiAttr("pseudo_flc", true)]
        public bool pseudo_flc = false;

        PIDController flc_controller = new PIDController();

        [VesselSerializable("flc_pid_Kp")]
        [AutoGuiAttr("flc_pid_Kp", true, "G4")]
        public double flc_pid_Kp { get { return flc_controller.KP; } set { flc_controller.KP = value; } }

        [VesselSerializable("flc_pid_Ki")]
        [AutoGuiAttr("flc_pid_Ki", true, "G4")]
        public double flc_pid_Ki { get { return flc_controller.KI; } set { flc_controller.KI = value; } }

        [VesselSerializable("flc_pid_Kd")]
        [AutoGuiAttr("flc_pid_Kd", true, "G4")]
        public double flc_pid_Kd { get { return flc_controller.KD; } set { flc_controller.KD = value; } }

        [VesselSerializable("strength_mult")]
        [AutoGuiAttr("strength_mult", true, "G5")]
        public double strength_mult = 0.75;

        [VesselSerializable("height_relax_time")]
        [AutoGuiAttr("height_relax_time", true, "G5")]
        public double height_relax_time = 6.0;

        [VesselSerializable("height_relax_Kp")]
        [AutoGuiAttr("height_relax_Kp", true, "G5")]
        public double height_relax_Kp = 0.3;

        [VesselSerializable("max_climb_angle")]
        [AutoGuiAttr("max_climb_angle", true, "G5")]
        public double max_climb_angle = 30.0;

        public double dist_to_dest = 0.0;

        double filtered_drag = 0.0;

        Vector3d account_for_vertical_vel(Vector3d desired_direction)
        {
            Vector3d res = desired_direction.normalized * vessel.horizontalSrfSpeed + planet2vesNorm * desired_vertspeed;
            return res.normalized;
        }

        Vector3d account_for_fpa(Vector3d desired_direction)
        {
            Vector3d res = desired_direction.normalized * vessel.horizontalSrfSpeed + planet2vesNorm * vessel.horizontalSrfSpeed * Math.Tan(desired_vertspeed * dgr2rad);
            return res.normalized;
        }

        Vector3d account_for_height(Vector3d desired_direction)
        {
            double cur_alt = vessel.altitude;
            double height_error = desired_altitude - cur_alt;
            double acc = Vector3.Dot(imodel.gravity_acc + imodel.noninert_acc, -planet2vesNorm);    // free-fall vertical acceleration
            double height_relax_frame = 0.5 * acc * height_relax_time * height_relax_time;

            double relax_transition_k = 0.0;
            double des_vert_speed = 0.0;
            double relax_vert_speed = 0.0;
            Vector3d res = Vector3d.zero;

            Vector3d proportional_acc = Vector3d.zero;
            double cur_vert_speed = Vector3d.Dot(imodel.surface_v, planet2vesNorm);
            
            // speed control portion for ascent\descent
            double effective_max_climb_angle = max_climb_angle;

            if (Math.Abs(height_error) < height_relax_frame)
            {
                relax_transition_k = Common.Clamp(2.0 * (height_relax_frame - Math.Abs(height_error)), 0.0, 1.0);
                // we're in relaxation frame
                relax_vert_speed = height_relax_Kp * height_error;
                // exponential descent
                if (cur_vert_speed * height_error > 0.0)
                    proportional_acc = -planet2vesNorm * height_relax_Kp * cur_vert_speed;
                flc_controller.clear();
            }
            else if (pseudo_flc)
            {
                // refactored flc using pid loop
                double flc_pid_control = flc_controller.Control(imodel.surface_v_magnitude, thrust_c.setpoint.mps(), TimeWarp.fixedDeltaTime);
                if (height_error >= 0.0)
                {
                    effective_max_climb_angle = Common.Clamp(-flc_pid_control, 0.1, max_climb_angle);
                    if (thrust_c.spd_control_enabled)
                        thrust_c.ForceThrottle(1.0f);
                }
                else
                {
                    effective_max_climb_angle = Common.Clamp(-flc_pid_control, -max_climb_angle, -0.1);
                    if (thrust_c.spd_control_enabled)
                        thrust_c.ForceThrottle(0.0f);
                }
                
            }
            else
            {
                effective_max_climb_angle *= Math.Max(0.0, Math.Min(1.0, vessel.srfSpeed / thrust_c.setpoint.mps()));
                flc_controller.clear();
            }

            // let's assume parabolic ascent\descend
            Vector3d parabolic_acc = Vector3d.zero;
            if (height_error >= 0.0)
            {
                des_vert_speed = Math.Sqrt(acc * height_error);
                if (cur_vert_speed > 0.0)
                    parabolic_acc = -planet2vesNorm * 0.5 * cur_vert_speed * cur_vert_speed / height_error;
            }
            else
            {
                double vert_acc_descent = 2.0 * Math.Min(-5.0, acc - dir_c.strength * strength_mult * dir_c.max_lift_acc * 0.5);
                des_vert_speed = -Math.Sqrt(vert_acc_descent * height_error);
                if (cur_vert_speed < 0.0)
                    parabolic_acc = -planet2vesNorm * 0.5 * cur_vert_speed * cur_vert_speed / height_error;
            }

            double max_vert_speed = vessel.horizontalSrfSpeed * Math.Tan(effective_max_climb_angle * dgr2rad);
            bool apply_acc = Math.Abs(des_vert_speed) < max_vert_speed;
            des_vert_speed = Common.Clamp(des_vert_speed, max_vert_speed);
            res = desired_direction.normalized * vessel.horizontalSrfSpeed + planet2vesNorm * Common.lerp(des_vert_speed, relax_vert_speed, relax_transition_k);
            if (apply_acc)
                desired_vert_acc = parabolic_acc * (1.0 - relax_transition_k) + proportional_acc * relax_transition_k;
            return res.normalized;
        }

        internal bool LevelFlightMode
        {
            get { return current_mode == CruiseMode.LevelFlight; }
            set
            {
                if (value)
                {
                    if (current_mode != CruiseMode.LevelFlight)
                    {
                        // let's set new circle axis
                        circle_axis = Vector3d.Cross(vessel.srf_velocity, vessel.GetWorldPos3D() - vessel.mainBody.position).normalized;
                    }
                    current_mode = CruiseMode.LevelFlight;
                }
            }
        }

        bool CourseHoldMode
        {
            get { return current_mode == CruiseMode.CourseHold; }
            set
            {
                if (value)
                {
                    if (Math.Abs(vessel.latitude) > 80.0)
                        return;
                    if (current_mode != CruiseMode.CourseHold)
                    {
                        // TODO
                    }
                    current_mode = CruiseMode.CourseHold;
                }
            }
        }

        internal bool WaypointMode
        {
            get { return current_mode == CruiseMode.Waypoint; }
            set
            {
                if (value)
                {
                    if (current_mode != CruiseMode.Waypoint)
                    {
                        if (this.Active)
                        {
                            circle_axis = Vector3d.Cross(vessel.srf_velocity, vessel.GetWorldPos3D() - vessel.mainBody.position).normalized;
							Vector3d world_target_pos = vessel.mainBody.GetWorldSurfacePosition(desired_latitude, desired_longitude, vessel.altitude);
	                        dist_to_dest = Vector3d.Distance(world_target_pos, vessel.ReferenceTransform.position);
                        }
						else
                            MessageManager.post_quick_message("Can't pick waypoint when the Cruise Flight controller is disabled");
                    }
                    current_mode = CruiseMode.Waypoint;
                }
            }
        }

        void select_target()
		{
			var target = vessel.targetObject?.GetVessel();
			if (target == null || target.mainBody != vessel.mainBody)
				MessageManager.post_quick_message("No target to select");
			else {
				if (!target.Landed()) MessageManager.post_quick_message($"target {target.vesselName} is not landed");
				current_waypt.longitude = target.longitude;
				current_waypt.latitude = target.latitude;
				Debug.Log($"[AtmosphereAutopilot] target lat {current_waypt.latitude} lon {current_waypt.longitude}");
				desired_latitude.Value = (float)current_waypt.latitude;
				desired_longitude.Value = (float)current_waypt.longitude;
				AtmosphereAutopilot.Instance.mainMenuGUIUpdate();
				WaypointMode = true;
				MessageManager.post_quick_message($"Waypoint now {target.vesselName}");
			}
		}

		void select_waypoint()
		{
			NavWaypoint navPoint = NavWaypoint.fetch;
			if (navPoint == null || !navPoint.IsActive || navPoint.Body != this.vessel.mainBody) {
				MessageManager.post_quick_message("No waypoint available");
			} else {
				current_waypt.longitude = navPoint.Longitude;
				current_waypt.latitude = navPoint.Latitude;
				Debug.Log($"[AtmosphereAutopilot] waypoint lat {current_waypt.latitude} lon {current_waypt.longitude}");
				desired_latitude.Value = (float)current_waypt.latitude;
				desired_longitude.Value = (float)current_waypt.longitude;
				AtmosphereAutopilot.Instance.mainMenuGUIUpdate();
				WaypointMode = true;
				MessageManager.post_quick_message("Waypoint selected");
			}
		}

			void start_picking_waypoint()
        {
            MapView.EnterMapView();
            MessageManager.post_quick_message("Pick waypoint");
            picking_waypoint = true;
        }

        internal bool picking_waypoint = false;

        static bool advanced_options = false;

        protected override void _drawGUI(int id)
        {
            close_button();
            GUILayout.BeginVertical();

            // cruise flight control modes

            LevelFlightMode = GUILayout.Toggle(LevelFlightMode, "Level", GUIStyles.toggleButtonStyle);

            GUILayout.Space(5.0f);

            CourseHoldMode = GUILayout.Toggle(CourseHoldMode, "Heading",    GUIStyles.toggleButtonStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label("desired course", GUIStyles.labelStyleLeft);
            desired_course.DisplayLayout(GUIStyles.textBoxStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(5.0f);

            string waypoint_btn_str;
            if (WaypointMode)
                waypoint_btn_str = "WPT " + (dist_to_dest / 1000.0).ToString("#0.0") + " km";
            else
                waypoint_btn_str = "Waypoint";
            WaypointMode = GUILayout.Toggle(WaypointMode, waypoint_btn_str,
                GUIStyles.toggleButtonStyle);

			GUILayout.BeginHorizontal();
			GUILayout.Label("map:", GUIStyles.labelStyleRight);
            if (GUILayout.Button("Pick", GUIStyles.toggleButtonStyle) && !picking_waypoint)
            {
                if (this.Active)
                    start_picking_waypoint();
                else
                    MessageManager.post_quick_message("Can't pick waypoint when the Cruise Flight controller is disabled");
            }
			GUILayout.Label("from:", GUIStyles.labelStyleRight);
			if (GUILayout.Button("Tgt", GUIStyles.toggleButtonStyle))
			{
				if (this.Active)
					select_target();
				else
					MessageManager.post_quick_message("Can't select target when the Cruise Flight controller is disabled");
			}
			if (GUILayout.Button("Wpt", GUIStyles.toggleButtonStyle))
			{
				if (this.Active)
					select_waypoint();
				else
					MessageManager.post_quick_message("Can't select waypoint when the Cruise Flight controller is disabled");
			}
			GUILayout.EndHorizontal();

	        GUILayout.BeginHorizontal();
	        desired_latitude.DisplayLayout(GUIStyles.textBoxStyle);//, GUILayout.Width(60.0f));
	        desired_longitude.DisplayLayout(GUIStyles.textBoxStyle);//, GUILayout.Width(60.0f));
	        GUILayout.EndHorizontal();

			GUILayout.Space(10.0f);

            // speed

            thrust_c.SpeedCtrlGUIBlock();

            GUILayout.Space(10.0f);

            // vertical motion

            vertical_control = GUILayout.Toggle(vertical_control, "Vertical motion", GUIStyles.toggleButtonStyle);
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            if (GUILayout.Toggle(height_mode == HeightMode.Altitude, "Altitude", GUIStyles.toggleButtonStyle))
                height_mode = HeightMode.Altitude;
            desired_altitude.DisplayLayout(GUIStyles.textBoxStyle);
            GUILayout.EndVertical();
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(height_mode == HeightMode.VerticalSpeed, "V/S", GUIStyles.toggleButtonStyle))
            {
                if (height_mode == HeightMode.FlightPathAngle)
                {
                    desired_vertspeed.Value = (float) (vessel.horizontalSrfSpeed * Math.Tan(desired_vertspeed * dgr2rad));
                }
                height_mode = HeightMode.VerticalSpeed;
            }
                
            if (GUILayout.Toggle(height_mode == HeightMode.FlightPathAngle, "FPA", GUIStyles.toggleButtonStyle))
            {
                if (height_mode == HeightMode.VerticalSpeed)
                {
                    desired_vertspeed.Value = (float) (Math.Atan(desired_vertspeed / vessel.horizontalSrfSpeed) / dgr2rad);
                }
                height_mode = HeightMode.FlightPathAngle;
            }
                
            GUILayout.EndHorizontal();
            desired_vertspeed.DisplayLayout(GUIStyles.textBoxStyle);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.Space(10.0f);

            // status

            //GUILayout.Label("Status", GUIStyles.labelStyleCenter);
            //GUILayout.BeginHorizontal();
            //GUILayout.BeginVertical();
            //GUILayout.Label("Latitude", GUIStyles.labelStyleCenter);
            //GUILayout.Label(vessel.latitude.ToString("G6"), GUIStyles.labelStyleCenter);
            //GUILayout.EndVertical();
            //GUILayout.BeginVertical();
            //GUILayout.Label("Longitude", GUIStyles.labelStyleCenter);
            //GUILayout.Label(vessel.longitude.ToString("G7"), GUIStyles.labelStyleCenter);
            //GUILayout.EndVertical();
            //GUILayout.BeginVertical();
            //if (WaypointMode)
            //{
            //    GUILayout.Label("Dist (km)", GUIStyles.labelStyleCenter);
            //    GUILayout.Label((dist_to_dest / 1000.0).ToString("#0.0"), GUIStyles.labelStyleCenter);
            //}
            //else
            //{
            //    GUILayout.Label("Alt (m)", GUIStyles.labelStyleCenter);
            //    GUILayout.Label(vessel.altitude.ToString("G5") + " m", GUIStyles.labelStyleCenter);
            //}
            //GUILayout.EndVertical();
            //GUILayout.EndHorizontal();

            //GUILayout.Space(10.0f);

            // advanced options

            bool adv_o = advanced_options;
            advanced_options = GUILayout.Toggle(advanced_options, "Advanced options", GUIStyles.toggleButtonStyle);
            if (advanced_options)
            {
                GUILayout.Space(5.0f);
                AutoGUI.AutoDrawObject(this);
            }
            else if (adv_o)
                window.height = 100.0f;

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        public override void OnUpdate()
        {
            if (picking_waypoint)
            {
                if (!HighLogic.LoadedSceneIsFlight || !MapView.MapIsEnabled)
                {
                    // we left map without picking
                    MessageManager.post_quick_message("Cancelled");
                    picking_waypoint = false;
                    AtmosphereAutopilot.Instance.mainMenuGUIUpdate();
                    return;
                }
                // Thanks MechJeb!
                if (Input.GetMouseButtonDown(0) && !window.Contains(Input.mousePosition))
                {
                    Ray mouseRay = PlanetariumCamera.Camera.ScreenPointToRay(Input.mousePosition);
                    mouseRay.origin = ScaledSpace.ScaledToLocalSpace(mouseRay.origin);
                    Vector3d relOrigin = mouseRay.origin - vessel.mainBody.position;
                    Vector3d relSurfacePosition;
                    double curRadius = vessel.mainBody.pqsController.radiusMax;
                    if (PQS.LineSphereIntersection(relOrigin, mouseRay.direction, curRadius, out relSurfacePosition))
                    {
                        Vector3d surfacePoint = vessel.mainBody.position + relSurfacePosition;
                        current_waypt.longitude = vessel.mainBody.GetLongitude(surfacePoint);
                        current_waypt.latitude = vessel.mainBody.GetLatitude(surfacePoint);
                        picking_waypoint = false;

                        desired_latitude.Value = (float)current_waypt.latitude;
                        desired_longitude.Value = (float)current_waypt.longitude;

                        dist_to_dest = Vector3d.Distance(surfacePoint, vessel.ReferenceTransform.position);
                        AtmosphereAutopilot.Instance.mainMenuGUIUpdate();
	                    WaypointMode = true;
                        MessageManager.post_quick_message("Picked");
                    }
                    else
                    {
                        MessageManager.post_quick_message("Missed");
                    }
                }
            }
            else
            {
                if (Input.GetKeyDown(switch_key_mode))
                {
                    use_keys = !use_keys;
                    MessageManager.post_status_message(use_keys ? "CF key input mode enabled" : "CF key input mode disabled");
                }

                if (Input.GetKeyDown(vertical_control_key))
                {
                    vertical_control = !vertical_control;
                    MessageManager.post_status_message(use_keys ? "Vertical motion control enabled" : "Vertical motion control disabled");
                }

                if (Input.GetKeyDown(toggle_vertical_setpoint_type_key))
                {
                    height_mode = (HeightMode) (((int)height_mode + 1) % 3);
                    switch (height_mode)
                    {
                        case HeightMode.Altitude:
                            MessageManager.post_status_message("Altitude control");
                            break;
                        case HeightMode.VerticalSpeed:
                            MessageManager.post_status_message("Vertical speed control");
                            break;
                        case HeightMode.FlightPathAngle:
                            MessageManager.post_status_message("Flight path angle control");
                            break;
                    }
                    
                }

                // input shenanigans
                if (use_keys && !FlightDriver.Pause && InputLockManager.IsUnlocked(ControlTypes.PITCH)
                    && InputLockManager.IsUnlocked(ControlTypes.YAW))
                {
                    bool pitch_key_pressed = false;
                    float pitch_change_sign = 0.0f;
                    // Pitch
                    if (GameSettings.PITCH_UP.GetKey() && !GameSettings.MODIFIER_KEY.GetKey())
                    {
                        pitch_change_sign = 1.0f;
                        pitch_key_pressed = true;
                    }
                    else if (GameSettings.PITCH_DOWN.GetKey() && !GameSettings.MODIFIER_KEY.GetKey())
                    {
                        pitch_change_sign = -1.0f;
                        pitch_key_pressed = true;
                    }

                    if (pitch_key_pressed)
                    {
                        float setpoint;
                        float magnetic_mult;
                        float new_setpoint;
                        switch (height_mode)
                        {
                            case HeightMode.Altitude:
                                setpoint = desired_altitude;
                                new_setpoint = setpoint + pitch_change_sign * hotkey_altitude_sens * Time.deltaTime * setpoint;
                                desired_altitude.Value = new_setpoint;
                                break;
                            case HeightMode.VerticalSpeed:
                            case HeightMode.FlightPathAngle:
                                setpoint = desired_vertspeed;
                                magnetic_mult = Mathf.Abs(desired_vertspeed) < 10.0f ? 0.3f : 1.0f;
                                new_setpoint = setpoint + pitch_change_sign * hotkey_vertspeed_sens * Time.deltaTime * magnetic_mult;
                                desired_vertspeed.Value = new_setpoint;
                                break;
                        }

                        need_to_show_altitude = true;
                        altitude_change_counter = 0.0f;
                        AtmosphereAutopilot.Instance.mainMenuGUIUpdate();
                    }

                    // Yaw (Course)
                    bool yaw_key_pressed = false;
                    float yaw_change_sign = 0.0f;
                    if (GameSettings.YAW_RIGHT.GetKey() && !GameSettings.MODIFIER_KEY.GetKey())
                    {
                        yaw_key_pressed = true;
                        yaw_change_sign = 1.0f;
                    }
                    else if (GameSettings.YAW_LEFT.GetKey() && !GameSettings.MODIFIER_KEY.GetKey())
                    {
                        yaw_key_pressed = true;
                        yaw_change_sign = -1.0f;
                    }

                    if (yaw_key_pressed)
                    {
                        float setpoint = desired_course;
                        float new_setpoint = setpoint + yaw_change_sign * hotkey_course_sens * Time.deltaTime;
                        if (new_setpoint > 360.0f)
                            new_setpoint -= 360.0f;
                        if (new_setpoint < 0.0f)
                            new_setpoint = 360.0f + new_setpoint;
                        desired_course.Value = new_setpoint;
                        need_to_show_course = true;
                        course_change_counter = 0.0f;
                    }

                    if (need_to_show_course)
                        course_change_counter += Time.deltaTime;
                    if (course_change_counter > 1.0f)
                    {
                        course_change_counter = 0;
                        need_to_show_course = false;
                    }

                    if (need_to_show_altitude)
                    {
                        altitude_change_counter += Time.deltaTime;
                        if (height_mode != HeightMode.Altitude && altitude_change_counter > 0.2f)
                            if (Mathf.Abs(desired_vertspeed) < hotkey_vertspeed_snap)
                                desired_vertspeed.Value = 0.0f;
                    }
                    if (altitude_change_counter > 1.0f)
                    {
                        altitude_change_counter = 0;
                        need_to_show_altitude = false;
                    }
                }
                else
                {
                    need_to_show_altitude = false;
                    altitude_change_counter = 0;

                    need_to_show_course = false;
                    course_change_counter = 0;
                }
            }
        }

        [GlobalSerializable("use_keys")]
        [AutoGuiAttr("use keys", true)]
        public static bool use_keys = true;

        [GlobalSerializable("switch_key_mode")]
        [AutoHotkeyAttr("CF keys input mode")]
        static KeyCode switch_key_mode = KeyCode.RightAlt;

        [GlobalSerializable("vertical_control_key")]
        [AutoHotkeyAttr("CF vertical control")]
        static KeyCode vertical_control_key = KeyCode.None;

        [GlobalSerializable("toggle_vertical_setpoint_type_key")]
        [AutoHotkeyAttr("CF altitude\\vertical speed")]
        static KeyCode toggle_vertical_setpoint_type_key = KeyCode.None;

        bool need_to_show_course = false;
        float course_change_counter = 0.0f;

        bool need_to_show_altitude = false;
        float altitude_change_counter = 0.0f;

        [AutoGuiAttr("hotkey_course_speed", true, "G4")]
        [GlobalSerializable("hotkey_course_sens")]
        public static float hotkey_course_sens = 60.0f;

        [AutoGuiAttr("hotkey_altitude_speed", true, "G4")]
        [GlobalSerializable("hotkey_altitude_sens")]
        public static float hotkey_altitude_sens = 0.8f;

        [AutoGuiAttr("hotkey_vertspeed_speed", true, "G4")]
        [GlobalSerializable("hotkey_vertspeed_sens")]
        public static float hotkey_vertspeed_sens = 30.0f;

        [AutoGuiAttr("hotkey_vertspeed_snap", true, "G4")]
        [GlobalSerializable("hotkey_vertspeed_snap")]
        public static float hotkey_vertspeed_snap = 0.5f;

        protected override void OnGUICustomAlways()
        {
            if (need_to_show_course)
            {
                Rect rect = new Rect(Screen.width / 2.0f - 80.0f, 140.0f, 160.0f, 20.0f);
                string str = "course = " + desired_course.Value.ToString("G4");
                GUI.Label(rect, str, GUIStyles.hoverLabel);
            }
            if (need_to_show_altitude)
            {
                Rect rect = new Rect(Screen.width / 2.0f - 80.0f, 160.0f, 160.0f, 20.0f);
                string str = null;
                switch (height_mode)
                {
                    case HeightMode.Altitude:
                        str = "Altitude = " + desired_altitude.Value.ToString("G5");
                        break;
                    case HeightMode.VerticalSpeed:
                        str = "Vert speed = " + desired_vertspeed.Value.ToString("G4");
                        break;
                    case HeightMode.FlightPathAngle:
                        str = "FPA = " + desired_vertspeed.Value.ToString("G4");
                        break;
                }
                GUI.Label(rect, str, GUIStyles.hoverLabel);
            }

            desired_course.OnUpdate();
			// why didn't the desired_latitude constructor initialize this properly?  unknown
			// possibly the combination of VS2017 and .NET 3.3?
			desired_latitude.coord_format = DelayedFieldFloat.CoordFormat.NS;
	        desired_latitude.OnUpdate();
			desired_longitude.coord_format = DelayedFieldFloat.CoordFormat.EW;
	        desired_longitude.OnUpdate();
            desired_altitude.OnUpdate();
            desired_vertspeed.OnUpdate();
        }
    }
}
