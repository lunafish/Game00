using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

namespace CharacterCustomization
{
    [CustomEditor(typeof(SocketManager))]
    public class SocketManagerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            SocketManager manager = (SocketManager)target;

            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Socket Management", EditorStyles.boldLabel);

            if (GUILayout.Button("Refresh & Sync Sockets"))
            {
                manager.RefreshSockets();
                EditorUtility.SetDirty(manager);
            }

            var assignments = manager.GetEditorAssignments();

            if (assignments == null || assignments.Count == 0)
            {
                EditorGUILayout.HelpBox("No sockets found. Click 'Refresh Sockets' to search hierarchy.", MessageType.Info);
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                
                foreach (var assignment in assignments)
                {
                    EditorGUILayout.BeginHorizontal();
                    
                    // 소켓 이름 표시
                    EditorGUILayout.LabelField(assignment.socketName, GUILayout.Width(EditorGUIUtility.labelWidth));
                    
                    // 프리팹 할당 필드
                    assignment.prefab = (GameObject)EditorGUILayout.ObjectField(assignment.prefab, typeof(GameObject), false);
                    
                    // 인스턴스 존재 여부 표시 (읽기 전용)
                    GUI.enabled = false;
                    EditorGUILayout.ObjectField(assignment.currentInstance, typeof(GameObject), true, GUILayout.Width(100));
                    GUI.enabled = true;

                    EditorGUILayout.EndHorizontal();
                }

                if (EditorGUI.EndChangeCheck())
                {
                    EditorUtility.SetDirty(manager);
                }

                EditorGUILayout.Space();
                EditorGUILayout.BeginHorizontal();
                
                if (GUILayout.Button("Apply Assignments"))
                {
                    Undo.RegisterFullObjectHierarchyUndo(manager.gameObject, "Apply Socket Assignments");
                    manager.ApplyEditorAssignments();
                    EditorUtility.SetDirty(manager);
                }

                if (GUILayout.Button("Clear All Instances"))
                {
                    Undo.RegisterFullObjectHierarchyUndo(manager.gameObject, "Clear Socket Instances");
                    manager.DetachAllParts();
                    EditorUtility.SetDirty(manager);
                }

                EditorGUILayout.EndHorizontal();
            }
            
            if (GUI.changed)
            {
                serializedObject.ApplyModifiedProperties();
            }
        }
    }
}
