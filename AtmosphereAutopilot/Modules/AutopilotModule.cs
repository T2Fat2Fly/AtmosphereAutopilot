﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using UnityEngine;

namespace AtmosphereAutopilot
{

	/// <summary>
	/// Represents autopilot module, wich can be turned on and off.
	/// It has GUI and can be serialized. Derived classes need to implement
	/// OnActivate and OnDeactivate.
	/// </summary>
    public abstract class AutopilotModule : GUIWindow, ISerializable
    {
		public const int PITCH = 0;
		public const int ROLL = 1;
		public const int YAW = 2;

        protected Vessel vessel = null;
        protected bool enabled = false;
        protected string module_name;

        protected AutopilotModule(Vessel v, int wnd_id, string module_name):
            base(module_name, wnd_id, new Rect(50.0f, 80.0f, 220.0f, 50.0f))
        {
            vessel = v;
            this.module_name = module_name;
        }

		/// <summary>
		/// If this module is dependent on other modules, you should get references to
		/// required ones in this call.
		/// </summary>
		/// <param name="modules">Map of autopilot modules, instanced for this vessel.</param>
		public virtual void InitializeDependencies(Dictionary<Type, AutopilotModule> modules) { }

        public void Activate()
        {
            if (!enabled)
                OnActivate();
            enabled = true;
        }

        public string ModuleName { get { return module_name; } }

        protected abstract void OnActivate();

        public void Deactivate()
        {
            if (enabled)
                OnDeactivate();
            enabled = false;
        }

		protected abstract void OnDeactivate();

        public bool Active
        {
            get { return enabled; }
            set
            {
                if (value)
                    if (!enabled)
                        Activate();
                    else {}
                else
                    if (enabled)
                        Deactivate();
            }
        }

        #region Serialization

		/// <summary>
		/// Deserialize vessel-specific fields. Optional OnDeserialize callback is used.
		/// </summary>
        public bool DeserializeVesselSpecific()
        {
            return AutoSerialization.Deserialize(this, module_name.Replace(' ', '_'),
                KSPUtil.ApplicationRootPath + "GameData/AtmosphereAutopilot/" + vessel.vesselName + ".cfg",
                typeof(VesselSerializable), OnDeserialize);
        }

		/// <summary>
        /// Deserialize global fields. Optional OnDeserialize callback is used.
		/// </summary>
        public bool DeserializeGlobalSpecific()
        {
            return AutoSerialization.Deserialize(this, module_name.Replace(' ', '_'),
                KSPUtil.ApplicationRootPath + "GameData/AtmosphereAutopilot/Global_settings.cfg",
                typeof(GlobalSerializable), OnDeserialize);
        }

		/// <summary>
		/// Serialize global and vessel data to files. BeforeSerialized and OnSerialize callbacks are used.
		/// </summary>
        public virtual void Serialize()
        {
            BeforeSerialized();
            AutoSerialization.Serialize(this, module_name.Replace(' ', '_'),
                KSPUtil.ApplicationRootPath + "GameData/AtmosphereAutopilot/" + vessel.vesselName + ".cfg",
                typeof(VesselSerializable), OnSerialize);
            AutoSerialization.Serialize(this, module_name.Replace(' ', '_'),
                KSPUtil.ApplicationRootPath + "GameData/AtmosphereAutopilot/Global_settings.cfg",
                typeof(GlobalSerializable), OnSerialize);
        }

        protected virtual void BeforeSerialized() { }

        protected virtual void BeforeDeserialized() { }

		/// <summary>
		/// Deserialize global data and then vessel-specific. Optional BeforeDeserialized and OnDeserialize 
		/// callbacks are used.
		/// </summary>
		/// <returns>true if nothing crashed</returns>
        public virtual bool Deserialize()
        {
            BeforeDeserialized();
            return (DeserializeGlobalSpecific() & DeserializeVesselSpecific());
        }

        protected virtual void OnDeserialize(ConfigNode node, Type attribute_type) { }

        protected virtual void OnSerialize(ConfigNode node, Type attribute_type) { }

        #endregion
     
        #region GUI

        [GlobalSerializable("window_x")]
        protected float WindowLeft { get { return window.xMin; } set { window.xMin = value; } }

        [GlobalSerializable("window_y")]
		protected float WindowTop { get { return window.yMin; } set { window.yMin = value; } }

        [GlobalSerializable("window_width")]
		protected float WindowWidth { get { return window.width; } set { window.width = value; } }

		/// <inheritdoc />
        protected override void _drawGUI(int id)
        {
            GUILayout.BeginVertical();
            AutoGUI.AutoDrawObject(this);
            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        #endregion
    }
}
