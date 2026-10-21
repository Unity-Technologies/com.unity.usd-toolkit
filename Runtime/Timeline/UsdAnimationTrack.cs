using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Unity.USDToolkit.Timeline
{
    /// <summary>
    /// A Timeline track that drives a <see cref="UsdAnimationPlayer"/>: bind the player of an
    /// imported stage and each <see cref="UsdAnimationClip"/> on the track poses the stage at the
    /// clip's local time, in the Timeline window's preview as in Play Mode and players. Compiled
    /// only when com.unity.timeline is in the project.
    /// </summary>
    [TrackColor(0.36f, 0.62f, 0.9f)]
    [TrackBindingType(typeof(UsdAnimationPlayer))]
    [TrackClipType(typeof(UsdAnimationClip))]
    public sealed class UsdAnimationTrack : TrackAsset
    {
        // Registers what the bound player drives, so the Timeline window's preview restores it
        // when the preview ends instead of leaving the pose (and prefab overrides) in the scene.
        public override void GatherProperties(PlayableDirector director, IPropertyCollector driver)
        {
            var player = director != null ? director.GetGenericBinding(this) as UsdAnimationPlayer : null;
            if (player != null)
            {
                var transforms = new List<Transform>();
                var toggled = new List<GameObject>();
                var components = new List<Component>();
                player.CollectAnimatedTargets(transforms, toggled, components);
                foreach (Transform target in transforms)
                {
                    driver.AddFromComponent(target.gameObject, target);
                }

                foreach (GameObject target in toggled)
                {
                    driver.AddFromName(target, "m_IsActive");
                }

                // Blend-shape weights, and the vertex streams point caches draw through.
                foreach (Component component in components)
                {
                    driver.AddFromComponent(component.gameObject, component);
                }
            }

            base.GatherProperties(director, driver);
        }
    }
}
