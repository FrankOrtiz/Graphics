﻿using UnityEditor.ShaderGraph;
using ActionType = UnityEditor.ShaderGraph.IGraphDataAction;
using System;

namespace UnityEditor.ShaderGraph
{
    class DataStore<T>
    {
        Action<T, ActionType> m_Reducer;
        internal T State { get; private set; }

        internal Action<T> Subscribe;

        internal DataStore(Action<T, ActionType> reducer, T initialState)
        {
            m_Reducer = reducer;
            State = initialState;
        }

        internal void Dispatch(ActionType action)
        {
            m_Reducer(State, action);
            // Note: This would only work with reference types, as value types would require creating a new copy, this works given that we use GraphData which is a heap object
			// Notifies any listeners about change in state
            Subscribe?.Invoke(State);
        }
    }
}
