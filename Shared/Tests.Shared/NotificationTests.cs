////////////////////////////////////////////////////////////////////////////
//
// Copyright 2016 Realm Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
////////////////////////////////////////////////////////////////////////////

#if ENABLE_INTERNAL_NON_PCL_TESTS
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Realms;

namespace IntegrationTests
{
    [TestFixture, Preserve(AllMembers = true)]
#if WINDOWS
    [Ignore("Notifications are not implemented on Windows yet")]
#endif
    public class NotificationTests
    {
        private string _databasePath;
        private Realm _realm;

        private class OrderedContainer : RealmObject
        {
            public IList<OrderedObject> Items { get; }
        }

        private class OrderedObject : RealmObject
        {
            public int Order { get; set; }

            public bool IsPartOfResults { get; set; }

            public override string ToString()
            {
                return string.Format("[OrderedObject: Order={0}]", Order);
            }
        }

        [SetUp]
        public void SetUp()
        {
            _databasePath = Path.GetTempFileName();
            _realm = Realm.GetInstance(_databasePath);
        }

        [TearDown]
        public void TearDown()
        {
            _realm.Dispose();
            Realm.DeleteRealm(_realm.Config);
        }

        [Test]
        public void ShouldTriggerRealmChangedEvent()
        {
            // Arrange
            var wasNotified = false;
            _realm.RealmChanged += (sender, e) => { wasNotified = true; };

            // Act
            _realm.Write(() => _realm.CreateObject<Person>());

            // Assert
            Assert.That(wasNotified, "RealmChanged notification was not triggered");
        }

        [Test]
        public void RealmError_WhenNoSubscribers_OutputsMessageInConsole()
        {
            using (var sw = new StringWriter())
            {
                var original = Console.Error;
                Console.SetError(sw);
                _realm.NotifyError(new Exception());

                Assert.That(sw.ToString(), Contains.Substring("exception").And.ContainsSubstring("Realm.Error"));
                Console.SetError(original);
            }
        }

        [Test]
        public void ResultsShouldSendNotifications()
        {
            var query = _realm.All<Person>();
            ChangeSet changes = null;
            NotificationCallbackDelegate<Person> cb = (s, c, e) => changes = c;

            using (query.SubscribeForNotifications(cb))
            {
                _realm.Write(() => _realm.CreateObject<Person>());

                TestHelpers.RunEventLoop();
                Assert.That(changes, Is.Not.Null);
                Assert.That(changes.InsertedIndices, Is.EquivalentTo(new int[] { 0 }));
            }
        }

        [Test]
        public void ListShouldSendNotifications()
        {
            var container = new OrderedContainer();
            _realm.Write(() => _realm.Add(container));
            ChangeSet changes = null;
            NotificationCallbackDelegate<OrderedObject> cb = (s, c, e) => changes = c;
        
            using (container.Items.SubscribeForNotifications(cb))
            {
                _realm.Write(() => container.Items.Add(_realm.CreateObject<OrderedObject>()));
        
                TestHelpers.RunEventLoop();
                Assert.That(changes, Is.Not.Null);
                Assert.That(changes.InsertedIndices, Is.EquivalentTo(new int[] { 0 }));
            }
        }

        [Test]
        public void Results_WhenUnsubscribed_ShouldStopReceivingNotifications()
        {
            _realm.Write(() =>
            {
                _realm.Add(new OrderedObject
                {
                    Order = 0,
                    IsPartOfResults = true
                });
            });

            Exception error = null;
            _realm.Error += (sender, e) =>
            {
                error = e.Exception;
            };

            var query = _realm.All<OrderedObject>().Where(o => o.IsPartOfResults).OrderBy(o => o.Order).AsRealmCollection();
            var handle = GCHandle.Alloc(query); // prevent this from being collected across event loops
            try
            {
                // wait for the initial notification to come through
                TestHelpers.RunEventLoop();

                var eventArgs = new List<NotifyCollectionChangedEventArgs>();
                var handler = new NotifyCollectionChangedEventHandler((sender, e) => eventArgs.Add(e));

                var propertyEventArgs = new List<string>();
                var propertyHandler = new PropertyChangedEventHandler((sender, e) => propertyEventArgs.Add(e.PropertyName));

                query.CollectionChanged += handler;
                query.PropertyChanged += propertyHandler;

                Assert.That(error, Is.Null);

                _realm.Write(() =>
                {
                    _realm.Add(new OrderedObject
                    {
                        Order = 1,
                        IsPartOfResults = true
                    });
                });

                TestHelpers.RunEventLoop();
                
                Assert.That(error, Is.Null);
                Assert.That(eventArgs.Count, Is.EqualTo(1));
                Assert.That(eventArgs[0].Action, Is.EqualTo(NotifyCollectionChangedAction.Add));
                Assert.That(propertyEventArgs.Count, Is.EqualTo(2));
                Assert.That(propertyEventArgs, Is.EquivalentTo(new[] { "Count", "Item[]" }));

                _realm.Write(() =>
                {
                    _realm.Add(new OrderedObject
                    {
                        Order = 2,
                        IsPartOfResults = true
                    });
                });

                TestHelpers.RunEventLoop();

                Assert.That(error, Is.Null);
                Assert.That(eventArgs.Count, Is.EqualTo(2));
                Assert.That(eventArgs.All(e => e.Action == NotifyCollectionChangedAction.Add));
                Assert.That(propertyEventArgs.Count, Is.EqualTo(4));
                Assert.That(propertyEventArgs, Is.EquivalentTo(new[] { "Count", "Item[]", "Count", "Item[]" }));
                
                query.CollectionChanged -= handler;
                query.PropertyChanged -= propertyHandler;

                _realm.Write(() =>
                {
                    _realm.Add(new OrderedObject
                    {
                        Order = 3,
                        IsPartOfResults = true
                    });
                });

                TestHelpers.RunEventLoop();

                Assert.That(error, Is.Null);
                Assert.That(eventArgs.Count, Is.EqualTo(2));
                Assert.That(eventArgs.All(e => e.Action == NotifyCollectionChangedAction.Add));
                Assert.That(propertyEventArgs.Count, Is.EqualTo(4));
                Assert.That(propertyEventArgs, Is.EquivalentTo(new[] { "Count", "Item[]", "Count", "Item[]" }));
            }
            finally
            {
                handle.Free();
            }
        }

        [Test]
        public void Results_WhenTransactionHasBothAddAndRemove_ShouldReset()
        {
            // The INotifyCollectionChanged API doesn't have a mechanism to report both added and removed items,
            // as that would mess up the indices a lot. That's why when we have both removed and added items,
            // we should raise a Reset.
            var first = new OrderedObject
            {
                Order = 0,
                IsPartOfResults = true
            };
            _realm.Write(() =>
            {
                _realm.Add(first);
            });

            Exception error = null;
            _realm.Error += (sender, e) =>
            {
                error = e.Exception;
            };

            var query = _realm.All<OrderedObject>().Where(o => o.IsPartOfResults).OrderBy(o => o.Order).AsRealmCollection();
            var handle = GCHandle.Alloc(query); // prevent this from being collected across event loops
            try
            {
                // wait for the initial notification to come through
                TestHelpers.RunEventLoop();

                var eventArgs = new List<NotifyCollectionChangedEventArgs>();
                query.CollectionChanged += (sender, e) => eventArgs.Add(e);

                var propertyEventArgs = new List<string>();
                query.PropertyChanged += (sender, e) => propertyEventArgs.Add(e.PropertyName);

                Assert.That(error, Is.Null);

                _realm.Write(() =>
                {
                    _realm.Add(new OrderedObject
                    {
                        Order = 1,
                        IsPartOfResults = true
                    });

                    _realm.Remove(first);
                });

                TestHelpers.RunEventLoop();

                Assert.That(error, Is.Null);
                Assert.That(eventArgs.Count, Is.EqualTo(1));
                Assert.That(eventArgs[0].Action, Is.EqualTo(NotifyCollectionChangedAction.Reset));
                Assert.That(propertyEventArgs.Count, Is.EqualTo(2));
                Assert.That(propertyEventArgs, Is.EquivalentTo(new[] { "Count", "Item[]" }));
            }
            finally
            {
                handle.Free();
            }
        }

        [Test]
        public void List_WhenUnsubscribed_ShouldStopReceivingNotifications()
        {
            var container = new OrderedContainer();
            _realm.Write(() => _realm.Add(container));

            var eventArgs = new List<NotifyCollectionChangedEventArgs>();
            var handler = new NotifyCollectionChangedEventHandler((sender, e) =>
            {
                eventArgs.Add(e);
            });

            var propertyEventArgs = new List<string>();
            var propertyHandler = new PropertyChangedEventHandler((sender, e) => propertyEventArgs.Add(e.PropertyName));

            var collection = container.Items.AsRealmCollection();
            collection.CollectionChanged += handler;
            collection.PropertyChanged += propertyHandler;

            _realm.Write(() =>
            {
                container.Items.Add(new OrderedObject());
            });

            TestHelpers.RunEventLoop();

            Assert.That(eventArgs.Count, Is.EqualTo(1));
            Assert.That(eventArgs[0].Action, Is.EqualTo(NotifyCollectionChangedAction.Add));
            Assert.That(propertyEventArgs.Count, Is.EqualTo(2));
            Assert.That(propertyEventArgs, Is.EquivalentTo(new[] { "Count", "Item[]" }));
            
            collection.CollectionChanged -= handler;
            collection.PropertyChanged -= propertyHandler;
            
            _realm.Write(() =>
            {
                container.Items.Add(new OrderedObject());
            });

            TestHelpers.RunEventLoop();

            Assert.That(eventArgs.Count, Is.EqualTo(1));
            Assert.That(eventArgs[0].Action, Is.EqualTo(NotifyCollectionChangedAction.Add));
            Assert.That(propertyEventArgs.Count, Is.EqualTo(2));
            Assert.That(propertyEventArgs, Is.EquivalentTo(new[] { "Count", "Item[]" }));
        }

        [Test]
        public void List_WhenTransactionHasBothAddAndRemove_ShouldReset()
        {
            // The INotifyCollectionChanged API doesn't have a mechanism to report both added and removed items,
            // as that would mess up the indices a lot. That's why when we have both removed and added items,
            // we should raise a Reset.
            var container = new OrderedContainer();
            container.Items.Add(new OrderedObject());
            _realm.Write(() => _realm.Add(container));

            var eventArgs = new List<NotifyCollectionChangedEventArgs>();
            var propertyEventArgs = new List<string>();

            var collection = container.Items.AsRealmCollection();
            collection.CollectionChanged += (sender, e) => eventArgs.Add(e);
            collection.PropertyChanged += (sender, e) => propertyEventArgs.Add(e.PropertyName);

            _realm.Write(() =>
            {
                container.Items.Clear();
                container.Items.Add(new OrderedObject());
            });

            TestHelpers.RunEventLoop();

            Assert.That(eventArgs.Count, Is.EqualTo(1));
            Assert.That(eventArgs[0].Action, Is.EqualTo(NotifyCollectionChangedAction.Reset));
            Assert.That(propertyEventArgs.Count, Is.EqualTo(2));
            Assert.That(propertyEventArgs, Is.EquivalentTo(new[] { "Count", "Item[]" }));
        }

        [TestCaseSource(nameof(CollectionChangedTestCases))]
        public void TestRealmListNotifications(int[] initial, NotifyCollectionChangedAction action, int[] change, int startIndex)
        {
            var container = new OrderedContainer();
            foreach (var i in initial)
            {
                container.Items.Add(new OrderedObject { Order = i });
            }

            _realm.Write(() => _realm.Add(container));

            Exception error = null;
            _realm.Error += (sender, e) =>
            {
                error = e.Exception;
            };

            var collection = container.Items.AsRealmCollection();

            var eventArgs = new List<NotifyCollectionChangedEventArgs>();
            var propertyEventArgs = new List<string>();

            collection.CollectionChanged += (o, e) => eventArgs.Add(e);
            collection.PropertyChanged += (o, e) => propertyEventArgs.Add(e.PropertyName);

            Assert.That(error, Is.Null);
            _realm.Write(() =>
            {
                if (action == NotifyCollectionChangedAction.Add)
                {
                    foreach (var value in change)
                    {
                        container.Items.Add(new OrderedObject
                        {
                            Order = value
                        });
                    }
                }
                else if (action == NotifyCollectionChangedAction.Remove)
                {
                    foreach (var value in change)
                    {
                        container.Items.Remove(_realm.All<OrderedObject>().Single(o => o.Order == value));
                    }
                }
            });

            TestHelpers.RunEventLoop();
            Assert.That(error, Is.Null);

            Assert.That(eventArgs.Count, Is.EqualTo(1));
            var arg = eventArgs[0];
            if (action == NotifyCollectionChangedAction.Add)
            {
                Assert.That(arg.Action == action);
                Assert.That(arg.NewStartingIndex, Is.EqualTo(initial.Length));
                Assert.That(arg.NewItems.Cast<OrderedObject>().Select(o => o.Order), Is.EquivalentTo(change));
            }
            else if (action == NotifyCollectionChangedAction.Remove)
            {
                if (startIndex < 0)
                {
                    Assert.That(arg.Action == NotifyCollectionChangedAction.Reset);
                }
                else
                {
                    Assert.That(arg.Action == action);
                    Assert.That(arg.OldStartingIndex, Is.EqualTo(startIndex));
                    Assert.That(arg.OldItems.Count, Is.EqualTo(change.Length));
                }
            }

            Assert.That(propertyEventArgs.Count, Is.EqualTo(2));
            Assert.That(propertyEventArgs, Is.EquivalentTo(new[] { "Count", "Item[]" }));
        }

        [TestCaseSource(nameof(CollectionChangedTestCases))]
        public void TestCollectionChangedAdapter(int[] initial, NotifyCollectionChangedAction action, int[] change, int startIndex)
        {
            _realm.Write(() =>
            {
                foreach (var value in initial)
                {
                    var obj = _realm.CreateObject<OrderedObject>();
                    obj.Order = value;
                    obj.IsPartOfResults = true;
                }
            });

            Exception error = null;
            _realm.Error += (sender, e) =>
            {
                error = e.Exception;
            };

            var query = _realm.All<OrderedObject>().Where(o => o.IsPartOfResults).OrderBy(o => o.Order).AsRealmCollection();
            var handle = GCHandle.Alloc(query); // prevent this from being collected across event loops

            try
            {
                // wait for the initial notification to come through
                TestHelpers.RunEventLoop();

                var eventArgs = new List<NotifyCollectionChangedEventArgs>();
                var propertyEventArgs = new List<string>();

                query.CollectionChanged += (o, e) => eventArgs.Add(e);
                query.PropertyChanged += (o, e) => propertyEventArgs.Add(e.PropertyName);

                Assert.That(error, Is.Null);
                _realm.Write(() =>
                {
                    if (action == NotifyCollectionChangedAction.Add)
                    {
                        foreach (var value in change)
                        {
                            var obj = _realm.CreateObject<OrderedObject>();
                            obj.Order = value;
                            obj.IsPartOfResults = true;
                        }
                    }
                    else if (action == NotifyCollectionChangedAction.Remove)
                    {
                        foreach (var value in change)
                        {
                            _realm.All<OrderedObject>().Single(o => o.Order == value).IsPartOfResults = false;
                        }
                    }
                });

                TestHelpers.RunEventLoop();
                Assert.That(error, Is.Null);

                Assert.That(eventArgs.Count, Is.EqualTo(1));
                var arg = eventArgs[0];
                if (startIndex < 0)
                {
                    Assert.That(arg.Action == NotifyCollectionChangedAction.Reset);
                }
                else
                {
                    Assert.That(arg.Action == action);
                    if (action == NotifyCollectionChangedAction.Add)
                    {
                        Assert.That(arg.NewStartingIndex, Is.EqualTo(startIndex));
                        Assert.That(arg.NewItems.Cast<OrderedObject>().Select(o => o.Order), Is.EquivalentTo(change));
                    }
                    else if (action == NotifyCollectionChangedAction.Remove)
                    {
                        Assert.That(arg.OldStartingIndex, Is.EqualTo(startIndex));
                        Assert.That(arg.OldItems.Count, Is.EqualTo(change.Length));
                    }
                }

                Assert.That(propertyEventArgs.Count, Is.EqualTo(2));
                Assert.That(propertyEventArgs, Is.EquivalentTo(new[] { "Count", "Item[]" }));
            }
            finally
            {
                handle.Free();
            }
        }

        public IEnumerable<TestCaseData> CollectionChangedTestCases()
        {
            yield return new TestCaseData(new int[] { }, NotifyCollectionChangedAction.Add, new int[] { 1 },  0);
            yield return new TestCaseData(new int[] { }, NotifyCollectionChangedAction.Add, new int[] { 1, 2, 3 }, 0);
            yield return new TestCaseData(new int[] { 1, 2, 3 }, NotifyCollectionChangedAction.Remove, new int[] { 1, 2, 3 }, 0);
            yield return new TestCaseData(new int[] { 1, 2, 3 }, NotifyCollectionChangedAction.Remove, new int[] { 2 }, 1);
            yield return new TestCaseData(new int[] { 1, 2, 3 }, NotifyCollectionChangedAction.Remove, new int[] { 1 }, 0);
            yield return new TestCaseData(new int[] { 1, 2, 3 }, NotifyCollectionChangedAction.Add, new int[] { 0 }, 0);
            yield return new TestCaseData(new int[] { 1, 2, 3 }, NotifyCollectionChangedAction.Add, new int[] { 4 }, 3);
            yield return new TestCaseData(new int[] { 1, 2, 3 }, NotifyCollectionChangedAction.Add, new int[] { 4, 5 }, 3);
            yield return new TestCaseData(new int[] { 1, 2, 3, 4, 5 }, NotifyCollectionChangedAction.Remove, new int[] { 3, 4 }, 2);

            // When we have non-consecutive adds/removes, we should raise Reset, indicated by -1 here.
            yield return new TestCaseData(new int[] { 1, 3, 5 }, NotifyCollectionChangedAction.Add, new int[] { 2, 4 }, -1);
            yield return new TestCaseData(new int[] { 1, 2, 3, 4, 5 }, NotifyCollectionChangedAction.Remove, new int[] { 2, 4 }, -1);
        }
    }
}

#endif  // #if ENABLE_INTERNAL_NON_PCL_TESTS
