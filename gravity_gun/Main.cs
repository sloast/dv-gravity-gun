using System;
using System.Reflection;
using HarmonyLib;
using UnityModManagerNet;

using UnityEngine;
using DV;
using static UnityModManagerNet.UnityModManager;
using System.IO;
using dnlib;
using DV.Teleporters;
using System.Collections.Generic;
using VRTK.Examples;
using DV.Logic.Job;

namespace gravity_gun;

#if DEBUG
[EnableReloading]
#endif
public static class Main
{
	private static Harmony? harmony;
	public static ModEntry mod = null!;
	public static Settings settings = new();
	public static ModEntry.ModLogger logger { get {
		return mod.Logger;
	}}

	public static bool Load(UnityModManager.ModEntry modEntry)
	{
		try
		{
			mod = modEntry;
			modEntry.OnUnload = Unload;
			settings = Settings.Load<Settings>(modEntry);

			modEntry.OnGUI = OnGUI;
			modEntry.OnSaveGUI = OnSaveGUI;

			harmony = new Harmony(modEntry.Info.Id);
			harmony.PatchAll(Assembly.GetExecutingAssembly());

		}
		catch (Exception ex)
		{
			modEntry.Logger.LogException($"Failed to load {modEntry.Info.DisplayName}:", ex);
			harmony?.UnpatchAll(modEntry.Info.Id);
			return false;
		}

		return true;
	}

	public static bool Unload(UnityModManager.ModEntry modEntry)
	{
		harmony?.UnpatchAll(mod?.Info.Id);

		return true;
	}
	static void OnGUI(UnityModManager.ModEntry modEntry)
	{
		settings?.Draw(modEntry);
	}

	static void OnSaveGUI(UnityModManager.ModEntry modEntry)
	{
		settings?.Save(modEntry);
	}

}

public class Settings : ModSettings, IDrawable
{
	[Draw("Base force (default 200000)")] public float baseForce = 200000f;
	[Draw("Push force (default 10)")] public float pushForce = 10f;
	[Draw("InstaDerail (default false)")] public bool derail = false;
	[Draw("Min offset to derail (default 10)")] public float derailForce = 10f;
	[Draw("Damping (default 0.4)")] public float damping = 0.4f;
	[Draw("Grab at CoM")] public bool useCoM = false;

	public override void Save(UnityModManager.ModEntry modEntry)
	{
		Save(this, modEntry);
	}

	public void OnChange() { }
}


internal class CommsRadioGravityGun : MonoBehaviour, ICommsRadioMode
{
	public ButtonBehaviourType ButtonBehaviour { get; private set; }
	public CommsRadioController radio = null!;
	public Transform signalOrigin = null!;
	public CommsRadioDisplay display = null!;
	private LayerMask trainCarMask;

	public float force = Main.settings.baseForce;
	private const float dist_mult = 1.1f;

	private bool useCoM;

	private State state = State.Aiming;

	private TrainCar? aimedCar = null;
	private TrainCar? grabbedCar = null;
	private Rigidbody? grabbedRb = null;
	private Vector3 grabLocalPos;
	private float grabDistance;
	private Material highlightMaterial = null!;

	private void Awake()
	{
		radio = GetComponent<CommsRadioController>();
		signalOrigin = radio.laserBeam.transform;
		display = radio.deleteControl.display;
	}

	private void Start()
	{
		trainCarMask = LayerMask.GetMask(["Train_Big_Collider"]);
		highlightMaterial = radio.deleteControl.selectionMaterial;
	}

	public bool ButtonACustomAction()
	{
		if (Input.GetKey(KeyCode.LeftControl))
		{
			if (force > 100) {
				float inc = Mathf.Pow(10, Mathf.Ceil(Mathf.Log10(force)) - 1);
				force = Mathf.Round(force / inc - 1) * inc; // we love floating point error
			}
		}
		else
		{
			grabDistance /= dist_mult;
			if (grabDistance < 1)
			{
				grabDistance = 1;
			}
		}
		UpdateDisplay();

		return true;
	}


	public bool ButtonBCustomAction()
	{
		if (Input.GetKey(KeyCode.LeftControl))
		{
			float inc = Mathf.Pow(10, Mathf.Floor(Mathf.Log10(force)));
			force += inc;
		}
		else
		{
			grabDistance *= dist_mult;
		}
		UpdateDisplay();

		return true;
	}
	private void UpdateDisplay()
	{
		switch (state) {
			case State.Grabbed:
				display.SetDisplay("Grabbed", $"Car: {grabbedCar?.ID}\nDistance: {grabDistance}\nForce: {force}", "Release");
				break;

			case State.Aiming:
				SetStartingDisplay();
				break;
		}
	}

	public void Disable()
	{
		state = State.Aiming;
		ButtonBehaviour = ButtonBehaviourType.Regular;
		SetStartingDisplay();
		ClearHighlightCar();
		aimedCar = null;
		grabbedCar = null;
		grabbedRb = null;
	}

	public void Enable() {}

	public Color GetLaserBeamColor()
	{
		return new Color(1.0f, 0.5f, 0.0f);
	}

	private bool AimAtCar(bool select = false)
	{
		RaycastHit hit;

		if (!Physics.Raycast(signalOrigin.position, signalOrigin.forward, out hit, 500f, trainCarMask))
		{
			aimedCar = null;
			return false;
		}

		aimedCar = TrainCar.Resolve(hit.transform.root);

		if (select && aimedCar is not null)
		{
			grabbedCar = aimedCar;
			grabDistance = useCoM ? (grabbedCar.transform.position - signalOrigin.position).magnitude : hit.distance;
			grabbedRb = grabbedCar.GetComponent<Rigidbody>();
			grabLocalPos = grabbedCar.transform.InverseTransformPoint(hit.point);
		}

		return aimedCar is not null;
	}

	private void HighlightCar(TrainCar car)
	{
		MethodInfo dynMethod = typeof(CommsRadioCarDeleter).GetMethod("HighlightCar",
			BindingFlags.NonPublic | BindingFlags.Instance);
		dynMethod.Invoke(radio.deleteControl, [car, highlightMaterial]);
	}

	private void ClearHighlightCar()
	{
		MethodInfo dynMethod = typeof(CommsRadioCarDeleter).GetMethod("ClearHighlightCar",
			BindingFlags.NonPublic | BindingFlags.Instance);
		dynMethod.Invoke(radio.deleteControl, []);
	}

	public void OnUpdate()
	{
		switch (state)
		{
			case State.Aiming:
				TrainCar? previousAimedCar = aimedCar;
				if (AimAtCar())
				{
					if (previousAimedCar != aimedCar){
						HighlightCar(aimedCar!);
						display.SetAction("Grab");
					}
				}
				else if (previousAimedCar != null)
				{
					ClearHighlightCar();
					display.SetAction("");
				}
				break;

			case State.Grabbed:
				useCoM = Main.settings.useCoM;

				Vector3 carPosition = useCoM ? grabbedCar!.transform.position : grabbedCar!.transform.TransformPoint(grabLocalPos);
				Vector3 aimPosition = signalOrigin.position + (signalOrigin.forward * grabDistance);

				Vector3 diff = aimPosition - carPosition;

				if (diff.y > Main.settings.derailForce && !grabbedCar.derailed) grabbedCar.Derail();

				if (useCoM)
				{
					grabbedRb?.AddForce(diff * force);
					grabbedRb?.AddForce(-grabbedRb.velocity * force * Main.settings.damping); // damping
				}
				else
				{
					grabbedRb?.AddForceAtPosition(diff * force, carPosition);
					grabbedRb?.AddForceAtPosition(-grabbedRb.GetPointVelocity(carPosition) * force * Main.settings.damping, carPosition);
				}

				if (Input.GetMouseButtonDown(2)) // throw car
				{
					if (useCoM)
					{
						grabbedRb?.AddForce(signalOrigin.forward * force * Main.settings.pushForce, ForceMode.Impulse);
					}
					else
					{
						grabbedRb?.AddForceAtPosition(signalOrigin.forward * force * Main.settings.pushForce, carPosition, ForceMode.Impulse);
					}
					OnUse(); // release
				}

				break;
		}
	}

	public void OnUse()
	{
		useCoM = Main.settings.useCoM;
		switch (state)
		{
			case State.Aiming:
				if (!AimAtCar(true)) break;

				ClearHighlightCar();

				if (Main.settings.derail)
				{
					grabbedCar?.Derail();
				} else
				{
					grabbedCar?.ForceOptimizationState(false, false);
				}
				
				ButtonBehaviour = ButtonBehaviourType.Override;
				state = State.Grabbed;
				UpdateDisplay();

				break;

			case State.Grabbed:
				ButtonBehaviour = ButtonBehaviourType.Regular;
				state = State.Aiming;
				UpdateDisplay();
				aimedCar = null;

				break;
		}
	}

	public void OverrideSignalOrigin(Transform signalOrigin)
	{
		this.signalOrigin = signalOrigin;
	}

	public void SetStartingDisplay()
	{
		display.SetDisplay("Gravity Gun", "Aim at the vehicle you wish to  Y E E T.");
	}


	internal enum State
	{
		Aiming,
		Grabbed
	}
}

[HarmonyPatch(typeof(CommsRadioController))]
static class CommsRadioPatch
{
	[HarmonyPatch("Awake"), HarmonyPostfix]
	static void Postfix(CommsRadioController __instance, List<ICommsRadioMode> ___allModes)
	{
		var ggun = __instance.gameObject.AddComponent<CommsRadioGravityGun>();
		___allModes.Add(ggun);
	}
}
