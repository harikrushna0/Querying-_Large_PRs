// TaskDashboard.tsx
import React, { useEffect, useState } from 'react';

type TaskStatus = 'pending' | 'in-progress' | 'completed';

interface Task {
  id: number;
  title: string;
  description: string;
  status: TaskStatus;
  createdAt: string;
}

const dummyTasks: Task[] = Array.from({ length: 20 }, (_, i) => ({
  id: i + 1,
  title: `Task ${i + 1}`,
  description: `This is the description for Task ${i + 1}.`,
  status: ['pending', 'in-progress', 'completed'][i % 3] as TaskStatus,
  createdAt: new Date().toISOString(),
}));

const TaskDashboard: React.FC = () => {
  const [tasks, setTasks] = useState<Task[]>([]);
  const [search, setSearch] = useState('');
  const [filterStatus, setFilterStatus] = useState<TaskStatus | 'all'>('all');
  const [loading, setLoading] = useState<boolean>(true);

  useEffect(() => {
    setTimeout(() => {
      setTasks(dummyTasks);
      setLoading(false);
    }, 1000);
  }, []);

  const handleSearchChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    setSearch(e.target.value);
  };

  const handleStatusChange = (status: TaskStatus | 'all') => {
    setFilterStatus(status);
  };

  const filteredTasks = tasks.filter((task) => {
    const matchesSearch = task.title.toLowerCase().includes(search.toLowerCase());
    const matchesStatus = filterStatus === 'all' || task.status === filterStatus;
    return matchesSearch && matchesStatus;
  });

  const getStatusColor = (status: TaskStatus) => {
    switch (status) {
      case 'pending':
        return 'bg-yellow-200 text-yellow-800';
      case 'in-progress':
        return 'bg-blue-200 text-blue-800';
      case 'completed':
        return 'bg-green-200 text-green-800';
    }
  };

  return (
    <div className="p-6 max-w-5xl mx-auto">
      <h1 className="text-3xl font-bold mb-4">Task Dashboard</h1>

      <div className="mb-6 flex flex-col md:flex-row md:items-center md:justify-between gap-4">
        <input
          type="text"
          placeholder="Search by title..."
          value={search}
          onChange={handleSearchChange}
          className="border border-gray-300 rounded-md px-4 py-2 w-full md:w-64"
        />

        <div className="flex gap-2 flex-wrap">
          {['all', 'pending', 'in-progress', 'completed'].map((status) => (
            <button
              key={status}
              className={`px-4 py-2 rounded-md border ${
                filterStatus === status
                  ? 'bg-blue-600 text-white'
                  : 'bg-white text-gray-700'
              }`}
              onClick={() => handleStatusChange(status as TaskStatus | 'all')}
            >
              {status}
            </button>
          ))}
        </div>
      </div>

      {loading ? (
        <div className="text-center py-10 text-gray-500">Loading tasks...</div>
      ) : filteredTasks.length === 0 ? (
        <div className="text-center py-10 text-gray-500">No tasks found.</div>
      ) : (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {filteredTasks.map((task) => (
            <div key={task.id} className="p-4 border rounded-lg shadow-sm bg-white">
              <div className="flex justify-between items-start">
                <h2 className="text-xl font-semibold">{task.title}</h2>
                <span
                  className={`text-xs px-2 py-1 rounded-full ${getStatusColor(task.status)}`}
                >
                  {task.status}
                </span>
              </div>
              <p className="mt-2 text-sm text-gray-600">{task.description}</p>
              <p className="mt-4 text-xs text-gray-400">
                Created at: {new Date(task.createdAt).toLocaleString()}
              </p>
            </div>
          ))}
        </div>
      )}
    </div>
  );
};

export default TaskDashboard;


