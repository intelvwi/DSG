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
using System.Linq;
using System.Text;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Framework;
using OpenMetaverse;
using System.IO;
using System.Xml;

namespace DSG.RegionSync
{
    public class PropertySerializer
    {
        //TO BE TESTED
        public static string GetPropertyHashValue(string initValue)
        {
            return Util.Md5Hash(initValue);
        }


        public class MyXmlTextWriter : XmlTextWriter
        {
            public MyXmlTextWriter(StringWriter sw)
                : base(sw)
            {

            }

            public override void WriteEndElement()
            {
                base.WriteFullEndElement();
            }
        }

        public static string SerializeShape(PrimitiveBaseShape shape)
        {
            string serializedShape;
            using (StringWriter sw = new StringWriter())
            {
                using (XmlTextWriter writer = new MyXmlTextWriter(sw))
                {
                    SceneObjectSerializer.WriteShape(writer, shape, new Dictionary<string, object>());
                }
                serializedShape = sw.ToString();
            }
            return serializedShape;
        }

        public static PrimitiveBaseShape DeSerializeShape(string shapeString)
        {
            if (shapeString == null || shapeString == String.Empty || shapeString == "")
            {
                return null;
            }
            StringReader sr = new StringReader(shapeString);
            XmlTextReader reader = new XmlTextReader(sr);
            PrimitiveBaseShape shapeValue;
            try
            {
                List<string> errors = new List<string>();
                shapeValue = SceneObjectSerializer.ReadShape(reader, "Shape", out errors);
            }
            catch (Exception e)
            {
                Console.WriteLine("DeSerializeShape: Error " + e.Message);
                return null;
            }
            return shapeValue;
        }

        public static string SerializeTaskInventory(TaskInventoryDictionary inventory, Scene scene)
        {
            string serializedTaskInventory;
            using (StringWriter sw = new StringWriter())
            {
                using (XmlTextWriter writer = new XmlTextWriter(sw))
                {
                    SceneObjectSerializer.WriteTaskInventory(writer, inventory, new Dictionary<string, object>(), scene);
                }
                serializedTaskInventory = sw.ToString();
            }
            return serializedTaskInventory;
        }

        public static TaskInventoryDictionary DeSerializeTaskInventory(string taskInvString)
        {
            if (taskInvString == null || taskInvString == String.Empty || taskInvString == "")
            {
                return null;
            }
            StringReader sr = new StringReader(taskInvString);
            XmlTextReader reader = new XmlTextReader(sr);
            TaskInventoryDictionary taskVal;
            try
            {
                taskVal = SceneObjectSerializer.ReadTaskInventory(reader, "TaskInventory");
            }
            catch (Exception e)
            {
                Console.WriteLine("DeSerializeTaskInventory: Error " + e.Message);
                return null;
            }
            return taskVal;
        }

        //Copy code from SceneObjectSerializer.SOPToXml2
        public static string SerializeColor(System.Drawing.Color color)
        {
            string serializedColor;
            using (StringWriter sw = new StringWriter())
            {
                using (XmlTextWriter writer = new XmlTextWriter(sw))
                {
                    writer.WriteStartElement("Color");
                    writer.WriteElementString("R", color.R.ToString(Utils.EnUsCulture));
                    writer.WriteElementString("G", color.G.ToString(Utils.EnUsCulture));
                    writer.WriteElementString("B", color.B.ToString(Utils.EnUsCulture));
                    writer.WriteElementString("A", color.A.ToString(Utils.EnUsCulture));
                    writer.WriteEndElement();
                }
                serializedColor = sw.ToString();
            }
            return serializedColor;
        }

        //Copy code from SceneObjectSerializer.ProcessColor
        public static System.Drawing.Color DeSerializeColor(string colorString)
        {
            StringReader sr = new StringReader(colorString);
            XmlTextReader reader = new XmlTextReader(sr);

            System.Drawing.Color color = new System.Drawing.Color();

            reader.ReadStartElement("Color");
            if (reader.Name == "R")
            {
                float r = reader.ReadElementContentAsFloat("R", String.Empty);
                float g = reader.ReadElementContentAsFloat("G", String.Empty);
                float b = reader.ReadElementContentAsFloat("B", String.Empty);
                float a = reader.ReadElementContentAsFloat("A", String.Empty);
                color = System.Drawing.Color.FromArgb((int)a, (int)r, (int)g, (int)b);
                reader.ReadEndElement();
            }
            return color;
        }

    }
}
