import React, { useState } from 'react';

const MyComponent = () => {
  const [user, setUser] = useState({ name: 'Harikrushna', age: 22 });
  const [notifications, setNotifications] = useState(['Welcome!', 'You have 2 messages']);
  const [isLoggedIn, setIsLoggedIn] = useState(true);
  const [theme, setTheme] = useState('light');

  const toggleTheme = () => {
    setTheme((prev) => (prev === 'light' ? 'dark' : 'light'));
  };

  const handleLogout = () => {
    setIsLoggedIn(false);
    setNotifications([]);
  };

  return (
    <div className={`container ${theme}`}>
      <header>
        <h1>Hello {user.name}</h1>
        <p>Age: {user.age}</p>
        <button onClick={toggleTheme}>Toggle Theme</button>
        {isLoggedIn && <button onClick={handleLogout}>Logout</button>}
      </header>

      <section>
        <h2>Notifications</h2>
        <ul>
          {notifications.length === 0 ? (
            <li>No new notifications</li>
          ) : (
            notifications.map((note, idx) => <li key={idx}>{note}</li>)
          )}
        </ul>
      </section>

      <section>
        <h2>Dashboard</h2>
        <div className="dashboard">
          <Card title="Profile" content="View and edit your profile." />
          <Card title="Settings" content="Update preferences and settings." />
          <Card title="Reports" content="Check your activity reports." />
        </div>
      </section>

      <footer>
        <small>&copy; 2025 All rights reserved</small>
      </footer>
    </div>
  );
};

const Card = ({ title, content }) => {
  const [hovered, setHovered] = useState(false);

  return (
    <div
      className={`card ${hovered ? 'hovered' : ''}`}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
    >
      <h3>{title}</h3>
      <p>{content}</p>
      {hovered && <button>Open</button>}
    </div>
  );
};

export default MyComponent;
