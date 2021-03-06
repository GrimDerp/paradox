// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.
using System;
using System.Collections.Generic;
using System.Linq;

namespace SiliconStudio.Assets.Templates
{
    /// <summary>
    /// Handle templates for creating <see cref="Package"/>, <see cref="ProjectReference"/>
    /// </summary>
    public class TemplateManager
    {
        private static readonly object ThisLock = new object();
        private static readonly List<ITemplateGenerator> Generators = new List<ITemplateGenerator>();

        /// <summary>
        /// Registers the specified factory.
        /// </summary>
        /// <param name="generator">The factory.</param>
        /// <exception cref="System.ArgumentNullException">factory</exception>
        public static void Register(ITemplateGenerator generator)
        {
            if (generator == null) throw new ArgumentNullException("generator");

            lock (ThisLock)
            {
                if (!Generators.Contains(generator))
                {
                    Generators.Add(generator);
                }
            }
        }

        /// <summary>
        /// Unregisters the specified factory.
        /// </summary>
        /// <param name="generator">The factory.</param>
        /// <exception cref="System.ArgumentNullException">factory</exception>
        public static void Unregister(ITemplateGenerator generator)
        {
            if (generator == null) throw new ArgumentNullException("generator");

            lock (ThisLock)
            {
                Generators.Remove(generator);
            }
        }

        /// <summary>
        /// Finds all template descriptions.
        /// </summary>
        /// <returns>IEnumerable&lt;TemplateGeneratorDescription&gt;.</returns>
        public static IEnumerable<TemplateDescription> FindTemplates()
        {
            // TODO this will not work if the same package has different versions
            return PackageStore.Instance.GetInstalledPackages().SelectMany(package => package.Templates).OrderBy(tpl => tpl.Order).ThenBy(tpl => tpl.Name);
        }

        /// <summary>
        /// Finds a template generator supporting the specified template description
        /// </summary>
        /// <param name="description">The description.</param>
        /// <returns>A template generator supporting the specified description or null if not found.</returns>
        public static ITemplateGenerator FindTemplateGenerator(TemplateDescription description)
        {
            if (description == null) throw new ArgumentNullException("description");
            lock (ThisLock)
            {
                // From most recently registered to older
                for (int i = Generators.Count - 1; i >=0 ; i--)
                {
                    var generator = Generators[i];
                    if (generator.IsSupportingTemplate(description))
                    {
                        return generator;
                    }
                }
            }
            return null;
        }
    }
}