﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Volo.Abp.Domain.Entities.Events;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.EventBus.Local;
using Volo.Abp.Modularity;
using Volo.Abp.TestApp.Domain;
using Volo.Abp.Uow;
using Xunit;

namespace Volo.Abp.TestApp.Testing;

public abstract class DomainEvents_Tests<TStartupModule> : TestAppTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{
    protected readonly IRepository<Person, Guid> PersonRepository;
    protected readonly IRepository<AppEntityWithNavigations, Guid> AppEntityWithNavigationsRepository;
    protected readonly ILocalEventBus LocalEventBus;
    protected readonly IDistributedEventBus DistributedEventBus;

    protected DomainEvents_Tests()
    {
        PersonRepository = GetRequiredService<IRepository<Person, Guid>>();
        AppEntityWithNavigationsRepository = GetRequiredService<IRepository<AppEntityWithNavigations, Guid>>();
        LocalEventBus = GetRequiredService<ILocalEventBus>();
        DistributedEventBus = GetRequiredService<IDistributedEventBus>();
    }

    [Fact]
    public virtual async Task Should_Publish_Events_In_Order()
    {
        bool testPersonCreateHandled = false;
        bool douglesUpdateHandled = false;
        bool douglesNameChangeHandled = false;
        bool customEventHandled = false;
        bool customEvent2Handled = false;

        LocalEventBus.Subscribe<EntityCreatedEventData<Person>>(data =>
        {
            data.Entity.Name.ShouldBe("TestPerson1");
            testPersonCreateHandled = true;
            douglesUpdateHandled.ShouldBeFalse();
            douglesNameChangeHandled.ShouldBeFalse();
            customEventHandled.ShouldBeFalse();
            customEvent2Handled.ShouldBeFalse();
            return Task.CompletedTask;
        });

        LocalEventBus.Subscribe<MyCustomEventData>(data =>
        {
            data.Value.ShouldBe("42");
            customEventHandled = true;
            testPersonCreateHandled.ShouldBeTrue();
            douglesUpdateHandled.ShouldBeFalse();
            douglesNameChangeHandled.ShouldBeFalse();
            customEvent2Handled.ShouldBeFalse();
            return Task.CompletedTask;
        });

        LocalEventBus.Subscribe<PersonNameChangedEvent>(data =>
        {
            data.OldName.ShouldBe("Douglas");
            data.Person.Name.ShouldBe("Douglas-Updated");
            douglesNameChangeHandled = true;
            testPersonCreateHandled.ShouldBeTrue();
            customEventHandled.ShouldBeTrue();
            douglesUpdateHandled.ShouldBeFalse();
            customEvent2Handled.ShouldBeFalse();
            return Task.CompletedTask;
        });

        LocalEventBus.Subscribe<EntityUpdatedEventData<Person>>(data =>
        {
            data.Entity.Name.ShouldBe("Douglas-Updated");
            douglesUpdateHandled = true;
            testPersonCreateHandled.ShouldBeTrue();
            customEventHandled.ShouldBeTrue();
            douglesNameChangeHandled.ShouldBeTrue();
            customEvent2Handled.ShouldBeFalse();
            return Task.CompletedTask;
        });

        LocalEventBus.Subscribe<MyCustomEventData2>(data =>
        {
            data.Value.ShouldBe("44");
            customEvent2Handled = true;
            testPersonCreateHandled.ShouldBeTrue();
            customEventHandled.ShouldBeTrue();
            douglesUpdateHandled.ShouldBeTrue();
            douglesNameChangeHandled.ShouldBeTrue();
            return Task.CompletedTask;
        });

        await WithUnitOfWorkAsync(new AbpUnitOfWorkOptions { IsTransactional = true }, async () =>
           {
               await PersonRepository.InsertAsync(
                   new Person(Guid.NewGuid(), "TestPerson1", 42)
               );

               await LocalEventBus.PublishAsync(new MyCustomEventData { Value = "42" });

               var douglas = await PersonRepository.GetAsync(TestDataBuilder.UserDouglasId);
               douglas.ChangeName("Douglas-Updated");
               await PersonRepository.UpdateAsync(douglas);

               await LocalEventBus.PublishAsync(new MyCustomEventData2 { Value = "44" });
           });
    }

    [Fact]
    public virtual async Task Should_Rollback_Uow_If_Event_Handler_Throws_Exception()
    {
        (await PersonRepository.FindAsync(x => x.Name == "TestPerson1")).ShouldBeNull();

        LocalEventBus.Subscribe<EntityCreatedEventData<Person>>(data =>
        {
            data.Entity.Name.ShouldBe("TestPerson1");
            throw new ApplicationException("Just to rollback the UOW");
        });

        var exception = await Assert.ThrowsAsync<ApplicationException>(async () =>
        {
            await WithUnitOfWorkAsync(new AbpUnitOfWorkOptions { IsTransactional = true }, async () =>
                   {
                await PersonRepository.InsertAsync(
                    new Person(Guid.NewGuid(), "TestPerson1", 42)
                );
            });
        });

        exception.Message.ShouldBe("Just to rollback the UOW");

        (await PersonRepository.FindAsync(x => x.Name == "TestPerson1")).ShouldBeNull();
    }

    [Fact]
    public async Task Should_Trigger_Domain_Events_For_Aggregate_Root()
    {
        //Arrange

        var isLocalEventTriggered = false;
        var isDistributedEventTriggered = false;

        LocalEventBus.Subscribe<PersonNameChangedEvent>(data =>
        {
            data.OldName.ShouldBe("Douglas");
            data.Person.Name.ShouldBe("Douglas-Changed");
            isLocalEventTriggered = true;
            return Task.CompletedTask;
        });

        DistributedEventBus.Subscribe<PersonNameChangedEto>(data =>
        {
            data.OldName.ShouldBe("Douglas");
            data.NewName.ShouldBe("Douglas-Changed");
            isDistributedEventTriggered = true;
            return Task.CompletedTask;
        });

        //Act

        await WithUnitOfWorkAsync(async () =>
        {
            var dougles = await PersonRepository.SingleAsync(b => b.Name == "Douglas");
            dougles.ChangeName("Douglas-Changed");
            await PersonRepository.UpdateAsync(dougles);
        });

        //Assert

        isLocalEventTriggered.ShouldBeTrue();
        isDistributedEventTriggered.ShouldBeTrue();
    }

    [Fact]
    public async Task Should_Trigger_Domain_Events_For_Aggregate_Root_When_Navigation_Changes_Tests()
    {
        var entityId = Guid.NewGuid();

        await AppEntityWithNavigationsRepository.InsertAsync(new AppEntityWithNavigations(entityId, "TestEntity"));

        var entityUpdatedEventTriggered = false;
        var personCreatedEventCount = 0;
        var entityUpdatedEventTriggerCount = 0;

        LocalEventBus.Subscribe<EntityCreatedEventData<Person>>(data =>
        {
            personCreatedEventCount++;
           return Task.CompletedTask;
        });

        LocalEventBus.Subscribe<EntityUpdatedEventData<AppEntityWithNavigations>>(async data =>
        {
            entityUpdatedEventTriggered = !entityUpdatedEventTriggered;
            await PersonRepository.InsertAsync(new Person(Guid.NewGuid(), Guid.NewGuid().ToString(), new Random().Next(1, 100)));
        });

        // Test with simple property
        await WithUnitOfWorkAsync(async () =>
        {
            var entity = await AppEntityWithNavigationsRepository.GetAsync(entityId);
            entity.Name = Guid.NewGuid().ToString();
            await AppEntityWithNavigationsRepository.UpdateAsync(entity);
        });
        entityUpdatedEventTriggered.ShouldBeTrue();
        personCreatedEventCount.ShouldBe(++entityUpdatedEventTriggerCount);

        LocalEventBus.Subscribe<EntityUpdatedEventData<AppEntityWithValueObjectAddress>>(data =>
        {
            throw new Exception("Should not trigger this event");
        });

        // Test with value object
        entityUpdatedEventTriggered = false;
        await WithUnitOfWorkAsync(async () =>
        {
            var entity = await AppEntityWithNavigationsRepository.GetAsync(entityId);
            entity.AppEntityWithValueObjectAddress = new AppEntityWithValueObjectAddress("Turkey");
            await AppEntityWithNavigationsRepository.UpdateAsync(entity);
        });
        entityUpdatedEventTriggered.ShouldBeTrue();
        personCreatedEventCount.ShouldBe(++entityUpdatedEventTriggerCount);

        entityUpdatedEventTriggered = false;
        await WithUnitOfWorkAsync(async () =>
        {
            var entity = await AppEntityWithNavigationsRepository.GetAsync(entityId);
            entity.AppEntityWithValueObjectAddress = null;
            await AppEntityWithNavigationsRepository.UpdateAsync(entity);
        });
        entityUpdatedEventTriggered.ShouldBeTrue();
        personCreatedEventCount.ShouldBe(++entityUpdatedEventTriggerCount);

        // Test with one to one
        entityUpdatedEventTriggered = false;
        await WithUnitOfWorkAsync(async () =>
        {
            var entity = await AppEntityWithNavigationsRepository.GetAsync(entityId);
            entity.OneToOne = new AppEntityWithNavigationChildOneToOne
            {
                ChildName = "ChildName"
            };
            await AppEntityWithNavigationsRepository.UpdateAsync(entity);
        });
        entityUpdatedEventTriggered.ShouldBeTrue();
        personCreatedEventCount.ShouldBe(++entityUpdatedEventTriggerCount);

        LocalEventBus.Subscribe<EntityUpdatedEventData<AppEntityWithNavigationChildOneToOne>>(data =>
        {
            throw new Exception("Should not trigger this event");
        });

        entityUpdatedEventTriggered = false;
        await WithUnitOfWorkAsync(async () =>
        {
            var entity = await AppEntityWithNavigationsRepository.GetAsync(entityId);
            entity.OneToOne = null;
            await AppEntityWithNavigationsRepository.UpdateAsync(entity);
        });
        entityUpdatedEventTriggered.ShouldBeTrue();
        personCreatedEventCount.ShouldBe(++entityUpdatedEventTriggerCount);

        // Test with one to many
        entityUpdatedEventTriggered = false;
        await WithUnitOfWorkAsync(async () =>
        {
            var entity = await AppEntityWithNavigationsRepository.GetAsync(entityId);
            entity.OneToMany = new List<AppEntityWithNavigationChildOneToMany>()
            {
                new AppEntityWithNavigationChildOneToMany
                {
                    AppEntityWithNavigationId = entity.Id,
                    ChildName = "ChildName1"
                }
            };
            await AppEntityWithNavigationsRepository.UpdateAsync(entity);
        });
        entityUpdatedEventTriggered.ShouldBeTrue();
        personCreatedEventCount.ShouldBe(++entityUpdatedEventTriggerCount);

        LocalEventBus.Subscribe<EntityUpdatedEventData<AppEntityWithNavigationChildOneToMany>>(data =>
        {
            throw new Exception("Should not trigger this event");
        });

        entityUpdatedEventTriggered = false;
        await WithUnitOfWorkAsync(async () =>
        {
            var entity = await AppEntityWithNavigationsRepository.GetAsync(entityId);
            entity.OneToMany.Clear();
            await AppEntityWithNavigationsRepository.UpdateAsync(entity);
        });
        entityUpdatedEventTriggered.ShouldBeTrue();
        personCreatedEventCount.ShouldBe(++entityUpdatedEventTriggerCount);

        // Test with many to many
        entityUpdatedEventTriggered = false;
        await WithUnitOfWorkAsync(async () =>
        {
            var entity = await AppEntityWithNavigationsRepository.GetAsync(entityId);
            entity.ManyToMany = new List<AppEntityWithNavigationChildManyToMany>()
            {
                new AppEntityWithNavigationChildManyToMany
                {
                    ChildName = "ChildName1"
                }
            };
            await AppEntityWithNavigationsRepository.UpdateAsync(entity);
        });
        entityUpdatedEventTriggered.ShouldBeTrue();
        personCreatedEventCount.ShouldBe(++entityUpdatedEventTriggerCount);

        entityUpdatedEventTriggered = false;
        await WithUnitOfWorkAsync(async () =>
        {
            var entity = await AppEntityWithNavigationsRepository.GetAsync(entityId);
            entity.ManyToMany.Clear();
            await AppEntityWithNavigationsRepository.UpdateAsync(entity);
        });
        entityUpdatedEventTriggered.ShouldBeTrue();
        personCreatedEventCount.ShouldBe(++entityUpdatedEventTriggerCount);
    }

    private class MyCustomEventData
    {
        public string Value { get; set; }
    }

    private class MyCustomEventData2
    {
        public string Value { get; set; }
    }
}

public abstract class AbpEntityChangeOptions_DomainEvents_Tests<TStartupModule> : TestAppTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{
    protected readonly IRepository<AppEntityWithNavigations, Guid> AppEntityWithNavigationsRepository;
    protected readonly ILocalEventBus LocalEventBus;

    protected AbpEntityChangeOptions_DomainEvents_Tests()
    {
        AppEntityWithNavigationsRepository = GetRequiredService<IRepository<AppEntityWithNavigations, Guid>>();
        LocalEventBus = GetRequiredService<ILocalEventBus>();
    }

    protected override void AfterAddApplication(IServiceCollection services)
    {
        services.Configure<AbpEntityChangeOptions>(options =>
        {
            options.PublishEntityUpdatedEventWhenNavigationChanges = false;
        });

        base.AfterAddApplication(services);
    }

    [Fact]
    public async Task Should_Not_Trigger_Domain_Events_For_Aggregate_Root_When_Navigation_Changes_Tests()
    {
        var entityId = Guid.NewGuid();
        await AppEntityWithNavigationsRepository.InsertAsync(new AppEntityWithNavigations(entityId, "TestEntity"));

        var entityUpdatedEventTriggered = false;

        LocalEventBus.Subscribe<EntityUpdatedEventData<AppEntityWithNavigations>>(data =>
        {
            entityUpdatedEventTriggered = !entityUpdatedEventTriggered;
            return Task.CompletedTask;
        });

        // Test with simple property
        await WithUnitOfWorkAsync(async () =>
        {
            var entity = await AppEntityWithNavigationsRepository.GetAsync(entityId);
            entity.Name = Guid.NewGuid().ToString();
            await AppEntityWithNavigationsRepository.UpdateAsync(entity);
        });
        entityUpdatedEventTriggered.ShouldBeTrue();

        // Test with value object
        entityUpdatedEventTriggered = false;
        await WithUnitOfWorkAsync(async () =>
        {
            var entity = await AppEntityWithNavigationsRepository.GetAsync(entityId);
            entity.AppEntityWithValueObjectAddress = new AppEntityWithValueObjectAddress("Turkey");
            await AppEntityWithNavigationsRepository.UpdateAsync(entity);
        });
        entityUpdatedEventTriggered.ShouldBeFalse();

        // Test with one to one
        entityUpdatedEventTriggered = false;
        await WithUnitOfWorkAsync(async () =>
        {
            var entity = await AppEntityWithNavigationsRepository.GetAsync(entityId);
            entity.OneToOne = new AppEntityWithNavigationChildOneToOne
            {
                ChildName = "ChildName"
            };
            await AppEntityWithNavigationsRepository.UpdateAsync(entity);
        });
        entityUpdatedEventTriggered.ShouldBeFalse();

        // Test with one to many
        entityUpdatedEventTriggered = false;
        await WithUnitOfWorkAsync(async () =>
        {
            var entity = await AppEntityWithNavigationsRepository.GetAsync(entityId);
            entity.OneToMany = new List<AppEntityWithNavigationChildOneToMany>()
            {
                new AppEntityWithNavigationChildOneToMany
                {
                    AppEntityWithNavigationId = entity.Id,
                    ChildName = "ChildName1"
                }
            };
            await AppEntityWithNavigationsRepository.UpdateAsync(entity);
        });
        entityUpdatedEventTriggered.ShouldBeFalse();

        // Test with many to many
        entityUpdatedEventTriggered = false;
        await WithUnitOfWorkAsync(async () =>
        {
            var entity = await AppEntityWithNavigationsRepository.GetAsync(entityId);
            entity.ManyToMany = new List<AppEntityWithNavigationChildManyToMany>()
            {
                new AppEntityWithNavigationChildManyToMany
                {
                    ChildName = "ChildName1"
                }
            };
            await AppEntityWithNavigationsRepository.UpdateAsync(entity);
        });
        entityUpdatedEventTriggered.ShouldBeFalse();
    }
}
